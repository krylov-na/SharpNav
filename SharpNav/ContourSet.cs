﻿#region License
/**
 * Copyright (c) 2013 Robert Rouhani <robert.rouhani@gmail.com> and other contributors (see CONTRIBUTORS file).
 * Licensed under the MIT License - https://raw.github.com/Robmaister/SharpNav/master/LICENSE
 */
#endregion

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpNav.Geometry;

namespace SharpNav
{
	public class ContourSet
	{
		private List<Contour> contours;
		
		private BBox3 bounds;
		private float cellSize;
		private float cellHeight;
		private int width;
		private int height;
		private int borderSize;

		public void MarkInternalEdges(ref int flag, int dir)
		{
			//flag represented as 4 bits (left bit represents dir = 3, right bit represents dir = 0)
			//default is 0000
			//the |= operation sets each direction bit to 1 (so if dir = 0, 0000 -> 0001)
			flag |= 1 << dir;
		}

		public int FlipAllBits (int flag)
		{
			//flips all the bits in res
			//0000 (completely internal) -> 1111
			//1111 (no internal edges) -> 0000
			return flag ^ 0xf;
		}

		public bool IsConnected(int flag, int dir)
		{
			//four bits, each bit represents a direction (0 = non-connected, 1 = connected)
			return (flag & (1 << dir)) != 0;
		}

		public void RemoveVisited(ref int flag, int dir)
		{
			//say flag = 0110
			//dir = 2 (so 1 << dir = 0100)
			//~dir = 1011
			//flag &= ~dir
			//flag = 0110 & 1011 = 0010
			flag &= ~(1 << dir); // remove visited edges
		}

		/// <summary>
		/// Create contours by tracing edges around the regions generated by the open heightfield.
		/// </summary>
		/// <param name="compactField">The <see cref="CompactHeightfield"/> provides the region data.</param>
		/// <param name="maxError">Amound of error allowed in simplification</param>
		/// <param name="maxEdgeLen">Limit the length of an edge.</param>
		/// <param name="buildFlags">Settings for how contours should be built.</param>
		public ContourSet(CompactHeightfield compactField, float maxError, int maxEdgeLen, ContourBuildFlags buildFlags)
		{
			//copy the OpenHeightfield data into ContourSet
			this.bounds = compactField.Bounds;

			if (compactField.BorderSize > 0)
			{
				//remove offset
				float pad = compactField.BorderSize * compactField.CellSize;
				this.bounds.Min.X += pad;
				this.bounds.Min.Z += pad;
				this.bounds.Max.X -= pad;
				this.bounds.Max.Z -= pad;
			}

			this.cellSize = compactField.CellSize;
			this.cellHeight = compactField.CellHeight;
			this.width = compactField.Width - compactField.BorderSize * 2;
			this.height = compactField.Height - compactField.BorderSize * 2;
			this.borderSize = compactField.BorderSize;

			int maxContours = Math.Max((int)compactField.MaxRegions, 8);
			contours = new List<Contour>(maxContours);

			int[] flags = new int[compactField.Spans.Length];

			//Modify flags array by using the OpenHeightfield data
			//mark boundaries
			for (int y = 0; y < compactField.Length; y++)
			{
				for (int x = 0; x < compactField.Width; x++)
				{
					//loop through all the spans in the cell
					CompactCell c = compactField.Cells[x + y * compactField.Width];
					for (int i = c.StartIndex, end = c.StartIndex + c.Count; i < end; i++)
					{
						int res = 0;
						CompactSpan s = compactField.Spans[i];

						//set the flag to 0 if the region is a border region or null.
						if (Region.IsBorderOrNull(compactField.Spans[i].Region))
						{
							flags[i] = 0;
							continue;
						}

						//go through all the neighboring cells
						for (int dir = 0; dir < 4; dir++)
						{
							//obtain region id
							int r = 0;
							if (s.IsConnected(dir))
							{
								int dx = x + MathHelper.GetDirOffsetX(dir);
								int dy = y + MathHelper.GetDirOffsetY(dir);
								int di = compactField.Cells[dx + dy * compactField.Width].StartIndex + CompactSpan.GetConnection(dir, ref s);
								r = compactField.Spans[di].Region;
							}

							//region ids are equal
							if (r == compactField.Spans[i].Region)
							{
								//res marks all the INTERNAL edges
								MarkInternalEdges(ref res, dir);
							}
						}

						//flags represents all the nonconnected edges, edges that are only internal
						//the edges need to be between different regions
						flags[i] = FlipAllBits(res); 
					}
				}
			}

			//Points are in format 
			//	(x, y, z) coordinates
			//	region id (if raw) / raw vertex index (if simplified)
			List<RawVertex> verts = new List<RawVertex>();
			List<SimplifiedVertex> simplified = new List<SimplifiedVertex>();

			int numContours = 0;
			for (int y = 0; y < compactField.Length; y++)
			{
				for (int x = 0; x < compactField.Width; x++)
				{
					CompactCell c = compactField.Cells[x + y * compactField.Width];
					for (int i = c.StartIndex, end = c.StartIndex + c.Count; i < end; i++)
					{
						//flags is either 0000 or 1111
						//in other words, not connected at all 
						//or has all connections, which means span is in the middle and thus not an edge.
						if (flags[i] == 0 || flags[i] == 0xf)
						{
							flags[i] = 0;
							continue;
						}

						int reg = compactField.Spans[i].Region;
						if (Region.IsBorderOrNull(reg))
							continue;
						
						AreaFlags area = compactField.Areas[i];
						
						//reset each iteration
						verts.Clear();
						simplified.Clear();

						//Mark points, which are basis of contous, intially with "verts"
						//Then, simplify "verts" to get "simplified"
						//Finally, clean up the "simplified" data
						WalkContour(x, y, i, compactField, flags, verts);
						SimplifyContour(verts, simplified, maxError, maxEdgeLen, buildFlags);
						RemoveDegenerateSegments(simplified);

						if (simplified.Count >= 3)
						{
							Contour cont = new Contour();
							
							//Save all the simplified and raw data in the Contour
							cont.Vertices = simplified.ToArray();

							if (borderSize > 0)
							{
								//remove offset
								for (int j = 0; j < cont.Vertices.Length; j++)
								{
									cont.Vertices[j].X -= borderSize;
									cont.Vertices[j].Z -= borderSize;
								}
							}

							//copy raw data
							cont.RawVertices = verts.ToArray();

							if (borderSize > 0)
							{
								//remove offset
								for (int j = 0; j < cont.RawVertices.Length; j++)
								{
									cont.RawVertices[j].X -= borderSize;
									cont.RawVertices[j].Z -= borderSize;
								}
							}

							cont.RegionId = reg;
							cont.Area = area;

							contours.Add(cont);
							numContours++;
						}
					}
				}
			}

			//Check and merge holes
			for (int i = 0; i < numContours; i++)
			{
				Contour cont = contours[i];

				//check if contour is backwards
				if (CalcAreaOfPolygon2D(cont.Vertices) < 0)
				{
					//find another contour with same region id
					int mergeIdx = -1;
					for (int j = 0; j < numContours; j++)
					{
						//don't compare to itself
						if (i == j) continue;

						//same region id
						if (contours[j].Vertices.Length != 0 && contours[j].RegionId == cont.RegionId)
						{
							//make sure polygon is correctly oriented
							if (CalcAreaOfPolygon2D(contours[j].Vertices) > 0)
							{
								mergeIdx = j;
								break;
							}
						}
					}

					//only merge if needed
					if (mergeIdx != -1)
					{
						Contour mcont = contours[mergeIdx];

						//merge by closest points
						int ia, ib;
						GetClosestIndices(mcont.Vertices, cont.Vertices, out ia, out ib);
						if (ia == -1 || ib == -1)
							continue;

						MergeContours(mcont, cont, ia, ib);
					}
				}
			}
		}

		public List<Contour> Contours
		{
			get
			{
				return contours;
			}
		}

		public BBox3 Bounds
		{
			get
			{
				return bounds;
			}
		}

		public float CellSize
		{
			get
			{
				return cellSize;
			}
		}

		public float CellHeight
		{
			get
			{
				return cellHeight;
			}
		}

		public int Width
		{
			get
			{
				return width;
			}
		}

		public int Height
		{
			get
			{
				return height;
			}
		}

		public int BorderSize
		{
			get
			{
				return borderSize;
			}
		}

		/// <summary>
		/// Initial generation of the contours
		/// </summary>
		/// <param name="x">Cell x</param>
		/// <param name="y">Cell y</param>
		/// <param name="i">Span index</param>
		/// <param name="openField">OpenHeightfield</param>
		/// <param name="flags">?</param>
		/// <param name="points">Vertices of contour</param>
		private void WalkContour(int x, int y, int i, CompactHeightfield openField, int[] flags, List<RawVertex> points)
		{
			int dir = 0;

			//find the first direction that has a connection 
			while (!IsConnected(flags[i], dir))
				dir++;

			int startDir = dir;
			int starti = i;

			AreaFlags area = openField.Areas[i];

			int iter = 0;
			while (++iter < 40000)
			{
				// this direction is connected
				if (IsConnected(flags[i], dir))
				{
					// choose the edge corner
					bool isBorderVertex;
					bool isAreaBorder = false;

					int px = x;
					int py = GetCornerHeight(x, y, i, dir, openField, out isBorderVertex);
					int pz = y;

					switch (dir)
					{
						case 0:
							pz++;
							break;
						
						case 1:
							px++;
							pz++;
							break;
						
						case 2:
							px++;
							break;
					}

					int r = 0;
					CompactSpan s = openField.Spans[i];
					if (s.IsConnected(dir))
					{
						int dx = x + MathHelper.GetDirOffsetX(dir);
						int dy = y + MathHelper.GetDirOffsetY(dir);
						int di = openField.Cells[dx + dy * openField.Width].StartIndex + CompactSpan.GetConnection(dir, ref s);
						r = openField.Spans[di].Region;
						if (area != openField.Areas[di])
							isAreaBorder = true;
					}
					
					// apply flags if neccessary
					if (isBorderVertex)
						Contour.SetBorderVertex(ref r);

					if (isAreaBorder)
						Contour.SetAreaBorder(ref r);
					
					//save the point
					points.Add(new RawVertex(px, py, pz, r));

					RemoveVisited(ref flags[i], dir);	// remove visited edges
					dir = (dir + 1) % 4;				// rotate clockwise
				}
				else
				{
					//get a new cell(x, y) and span index(i)
					int di = -1;
					int dx = x + MathHelper.GetDirOffsetX(dir);
					int dy = y + MathHelper.GetDirOffsetY(dir);
					
					CompactSpan s = openField.Spans[i];
					if (s.IsConnected(dir))
					{
						CompactCell dc = openField.Cells[dx + dy * openField.Width];
						di = dc.StartIndex + CompactSpan.GetConnection(dir, ref s);
					}
					
					if (di == -1)
					{
						// shouldn't happen
						return;
					}
					
					x = dx;
					y = dy;
					i = di;
					dir = (dir + 3) % 4; // rotate counterclockwise
				}

				if (starti == i && startDir == dir)
				{
					break;
				}
			}
		}

		/// <summary>
		/// Helper method for WalkContour function
		/// </summary>
		/// <param name="x">Cell x</param>
		/// <param name="y">Cell y</param>
		/// <param name="i">Span index i</param>
		/// <param name="dir">Direction (west, north, east, south)</param>
		/// <param name="openField">OpenHeightfield</param>
		/// <param name="isBorderVertex">Determine whether the vertex is a border or not</param>
		/// <returns></returns>
		private int GetCornerHeight(int x, int y, int i, int dir, CompactHeightfield openField, out bool isBorderVertex)
		{
			isBorderVertex = false;

			CompactSpan s = openField.Spans[i];
			int cornerHeight = s.Minimum;
			int dirp = (dir + 1) % 4; //new clockwise direction

			uint[] regs = { 0, 0, 0, 0 };

			//combine region and area codes in order to prevent border vertices, which are in between two areas, to be removed 
			regs[0] = (uint)(openField.Spans[i].Region | ((byte)openField.Areas[i] << 16));

			if (s.IsConnected(dir))
			{
				//get neighbor span
				int dx = x + MathHelper.GetDirOffsetX(dir);
				int dy = y + MathHelper.GetDirOffsetY(dir);
				int di = openField.Cells[dx + dy * openField.Width].StartIndex + CompactSpan.GetConnection(dir, ref s);
				CompactSpan ds = openField.Spans[di];

				cornerHeight = Math.Max(cornerHeight, ds.Minimum);
				regs[1] = (uint)(openField.Spans[di].Region | ((byte)openField.Areas[di] << 16));

				//get neighbor of neighbor's span
				if (ds.IsConnected(dirp))
				{
					int dx2 = dx + MathHelper.GetDirOffsetX(dirp);
					int dy2 = dy + MathHelper.GetDirOffsetY(dirp);
					int di2 = openField.Cells[dx2 + dy2 * openField.Width].StartIndex + CompactSpan.GetConnection(dirp, ref ds);
					CompactSpan ds2 = openField.Spans[di2];

					cornerHeight = Math.Max(cornerHeight, ds2.Minimum);
					regs[2] = (uint)(openField.Spans[di2].Region | ((byte)openField.Areas[di2] << 16));
				}
			}

			//get neighbor span
			if (s.IsConnected(dirp))
			{
				int dx = x + MathHelper.GetDirOffsetX(dirp);
				int dy = y + MathHelper.GetDirOffsetY(dirp);
				int di = openField.Cells[dx + dy * openField.Width].StartIndex + CompactSpan.GetConnection(dirp, ref s);
				CompactSpan ds = openField.Spans[di];

				cornerHeight = Math.Max(cornerHeight, ds.Minimum);
				regs[3] = (uint)(openField.Spans[di].Region | ((byte)openField.Areas[di] << 16));

				//get neighbor of neighbor's span
				if (ds.IsConnected(dir))
				{
					int dx2 = dx + MathHelper.GetDirOffsetX(dir);
					int dy2 = dy + MathHelper.GetDirOffsetY(dir);
					int di2 = openField.Cells[dx2 + dy2 * openField.Width].StartIndex + CompactSpan.GetConnection(dir, ref ds);
					CompactSpan ds2 = openField.Spans[di2];

					cornerHeight = Math.Max(cornerHeight, ds2.Minimum);
					regs[2] = (uint)(openField.Spans[di2].Region | ((byte)openField.Areas[di2] << 16));
				}
			}

			//check if vertex is special edge vertex
			//if so, these vertices will be removed later
			for (int j = 0; j < 4; j++)
			{
				int a = j;
				int b = (j + 1) % 4;
				int c = (j + 2) % 4;
				int d = (j + 3) % 4;

				//the vertex is a border vertex if:
				//two same exterior cells in a row followed by two interior cells and none of the regions are out of bounds

				bool twoSameExteriors = Region.IsBorder((int)regs[a]) && Region.IsBorder((int)regs[b]) && regs[a] == regs[b];
				bool twoSameInteriors = !Region.IsBorder((int)regs[c]) || !Region.IsBorder((int)regs[d]);
				bool intsSameArea = (regs[c] >> 16) == (regs[d] >> 16);
				bool noZeros = regs[a] != 0 && regs[b] != 0 && regs[c] != 0 && regs[d] != 0;
				if (twoSameExteriors && twoSameInteriors && intsSameArea && noZeros)
				{
					isBorderVertex = true;
					break;
				}
			}

			return cornerHeight;
		}

		/// <summary>
		/// Simplify the contours by reducing the number of edges
		/// </summary>
		/// <param name="points">Initial vertices</param>
		/// <param name="simplified">New and simplified vertices</param>
		private void SimplifyContour(List<RawVertex> points, List<SimplifiedVertex> simplified, float maxError, int maxEdgeLen, ContourBuildFlags buildFlags)
		{
			//add initial points
			bool hasConnections = false;
			for (int i = 0; i < points.Count; i++)
			{
				if (Contour.ExtractRegionId(points[i].RegionId) != 0)
				{
					hasConnections = true;
					break;
				}
			}

			if (hasConnections)
			{
				//contour has some portals to other regions
				//add new point to every location where region changes
				for (int i = 0, end = points.Count; i < end; i++)
				{
					int ii = (i + 1) % end;
					bool differentRegions = !Contour.IsSameRegion(points[i].RegionId, points[ii].RegionId);
					bool areaBorders = !Contour.IsSameArea(points[i].RegionId, points[ii].RegionId);
					
					if (differentRegions || areaBorders)
					{
						simplified.Add(new SimplifiedVertex(points[i].X, points[i].Y, points[i].Z, i));
					}
				}
			}

			//add some points if thhere are no connections
			if (simplified.Count == 0)
			{
				//find lower-left and upper-right vertices of contour
				int lowerLeftX = points[0].X;
				int lowerLeftY = points[0].Y;
				int lowerLeftZ = points[0].Z;
				int lowerLeftI = 0;
				
				int upperRightX = points[0].X;
				int upperRightY = points[0].Y;
				int upperRightZ = points[0].Z;
				int upperRightI = 0;
				
				//iterate through points
				for (int i = 0; i < points.Count; i++)
				{
					int x = points[i].X;
					int y = points[i].Y;
					int z = points[i].Z;
					
					if (x < lowerLeftX || (x == lowerLeftX && z < lowerLeftZ))
					{
						lowerLeftX = x;
						lowerLeftY = y;
						lowerLeftZ = z;
						lowerLeftI = i;
					}
					
					if (x > upperRightX || (x == upperRightX && z > upperRightZ))
					{
						upperRightX = x;
						upperRightY = y;
						upperRightZ = z;
						upperRightI = i;
					}
				}
				
				//save the points
				simplified.Add(new SimplifiedVertex(lowerLeftX, lowerLeftY, lowerLeftZ, lowerLeftI));
				simplified.Add(new SimplifiedVertex(upperRightX, upperRightY, upperRightZ, upperRightI));
			}

			//add points until all points are within erorr tolerance of simplified slope
			int numPoints = points.Count;
			for (int i = 0; i < simplified.Count;)
			{
				int ii = (i + 1) % simplified.Count;

				//obtain (x, z) coordinates, along with region id
				int ax = simplified[i].X;
				int az = simplified[i].Z;
				int ai = simplified[i].RawVertexIndex;

				int bx = simplified[ii].X;
				int bz = simplified[ii].Z;
				int bi = simplified[ii].RawVertexIndex;

				float maxDeviation = 0;
				int maxi = -1;
				int ci, cIncrement, endi;

				//traverse segment in lexilogical order (try to go from smallest to largest coordinates?)
				if (bx > ax || (bx == ax && bz > az))
				{
					cIncrement = 1;
					ci = (ai + cIncrement) % numPoints;
					endi = bi;
				}
				else
				{
					cIncrement = numPoints - 1;
					ci = (bi + cIncrement) % numPoints;
					endi = ai;
				}

				//tessellate only outer edges or edges between areas
				if (Contour.ExtractRegionId(points[ci].RegionId) == 0 || Contour.IsAreaBorder(points[ci].RegionId))
				{
					//find the maximum deviation
					while (ci != endi)
					{
						float deviation = MathHelper.DistanceFromPointToSegment2D(points[ci].X, points[ci].Z, ax, az, bx, bz);
						
						if (deviation > maxDeviation)
						{
							maxDeviation = deviation;
							maxi = ci;
						}

						ci = (ci + cIncrement) % numPoints;
					}
				}

				//If max deviation is larger than accepted error, add new point
				if (maxi != -1 && maxDeviation > (maxError * maxError))
				{
					//add extra space to list
					simplified.Add(new SimplifiedVertex(0, 0, 0, 0));

					//make space for new point by shifting elements to the right
					//ex: element at index 5 is now at index 6, since array[6] takes the value of array[6 - 1]
					for (int j = simplified.Count - 1; j > i; j--)
					{
						simplified[j] = simplified[j - 1];
					}

					//add point 
					simplified[i + 1] = new SimplifiedVertex(points[maxi], maxi);
				}
				else
				{
					i++;
				}
			}

			//split too long edges
			if (maxEdgeLen > 0 && Contour.CanTessellateEitherWallOrAreaEdges(buildFlags))
			{
				for (int i = 0; i < simplified.Count;)
				{
					int ii = (i + 1) % simplified.Count;

					//get (x, z) coordinates along with region id
					int ax = simplified[i].X;
					int az = simplified[i].Z;
					int ai = simplified[i].RawVertexIndex;

					int bx = simplified[ii].X;
					int bz = simplified[ii].Z;
					int bi = simplified[ii].RawVertexIndex;

					//find maximum deviation from segment
					int maxi = -1;
					int ci = (ai + 1) % numPoints;

					//tessellate only outer edges or edges between areas
					bool tess = false;

					//wall edges
					if (Contour.CanTessellateWallEdges(buildFlags) && Contour.ExtractRegionId(points[ci].RegionId) == 0)
						tess = true;

					//edges between areas
					if (Contour.CanTessellateAreaEdges(buildFlags) && Contour.IsAreaBorder(points[ci].RegionId))
						tess = true;

					if (tess)
					{
						int dx = bx - ax;
						int dz = bz - az;
						if (dx * dx + dz * dz > maxEdgeLen * maxEdgeLen)
						{
							//round based on lexilogical direction (smallest to largest cooridinates, first by x.
							//if x coordinates are equal, then compare z coordinates)
							int n = bi < ai ? (bi + numPoints - ai) : (bi - ai);
							
							if (n > 1)
							{
								if (bx > ax || (bx == ax && bz > az))
									maxi = (ai + n / 2) % numPoints;
								else
									maxi = (ai + (n + 1) / 2) % numPoints;
							}
						}
					}

					//add new point
					if (maxi != -1)
					{
						//add extra space to list
						simplified.Add(new SimplifiedVertex(0, 0, 0, 0));

						//make space for new point by shifting elements to the right
						//ex: element at index 5 is now at index 6, since array[6] takes the value of array[6 - 1]
						for (int j = simplified.Count - 1; j > i; j--)
						{
							simplified[j] = simplified[j - 1];
						}

						//add point
						simplified[i + 1] = new SimplifiedVertex(points[maxi], maxi);
					}
					else
					{
						i++;
					}
				}
			}

			for (int i = 0; i < simplified.Count; i++)
			{
				SimplifiedVertex sv = simplified[i];
				//take edge vertex flag from current raw point and neighbor region from next raw point
				int ai = (sv.RawVertexIndex + 1) % numPoints;
				int bi = sv.RawVertexIndex;

				//save new region id
				sv.RawVertexIndex = Contour.GetNewRegion(points[ai].RegionId, points[bi].RegionId);

				simplified[i] = sv;
			}
		}

		/// <summary>
		/// Clean up the simplified segments
		/// </summary>
		/// <param name="simplified"></param>
		private void RemoveDegenerateSegments(List<SimplifiedVertex> simplified)
		{
			//remove adjacent vertices which are equal on the xz-plane
			for (int i = 0; i < simplified.Count; i++)
			{
				int ni = i + 1;
				if (ni >= simplified.Count)
					ni = 0;

				if (simplified[i].X == simplified[ni].X &&
					simplified[i].Z == simplified[ni].Z)
				{
					//remove degenerate segment
					simplified.RemoveAt(i);
				}
			}
		}

		/// <summary>
		/// Determine whether a contour is going forwards (positive area) or backwards (negative area)
		/// </summary>
		/// <param name="verts">The vertex data</param>
		/// <returns></returns>
		private int CalcAreaOfPolygon2D(SimplifiedVertex[] verts)
		{
			int area = 0;
			for (int i = 0, j = verts.Length - 1; i < verts.Length; j = i++)
				area += verts[i].X * verts[j].Z - verts[j].X * verts[i].Z;

			return (area + 1) / 2; 
		}

		/// <summary>
		/// Required to find closest indices for merging.
		/// </summary>
		/// <param name="vertsA">First set of vertices</param>
		/// <param name="vertsB">Second set of vertices</param>
		/// <param name="indexA">First index</param>
		/// <param name="indexB">Second index</param>
		private void GetClosestIndices(SimplifiedVertex[] vertsA, SimplifiedVertex[] vertsB, out int indexA, out int indexB)
		{
			int closestDistance = int.MaxValue;
			int lengthA = vertsA.Length;
			int lengthB = vertsB.Length;

			indexA = -1;
			indexB = -1;

			for (int i = 0; i < lengthA; i++)
			{
				int vertA = i;
				int vertANext = (i + 1) % lengthA;
				int vertAPrev = (i + lengthA - 1) % lengthA;

				for (int j = 0; j < lengthB; j++)
				{
					int vertB = j; 
					
					//vertB must be infront of vertA
					if (ILeft(ref vertsA[vertAPrev], ref vertsA[vertA], ref vertsB[vertB]) && ILeft(ref vertsA[vertA], ref vertsA[vertANext], ref vertsB[vertB]))
					{
						int dx = vertsB[vertB].X - vertsA[vertA].X;
						int dz = vertsB[vertB].Z - vertsA[vertA].Z;
						int tempDist = dx * dx + dz * dz;
						if (tempDist < closestDistance)
						{
							indexA = i;
							indexB = j;
							closestDistance = tempDist;
						}
					}
				}
			}
		}

		/// <summary>
		/// Helper method for GetClosestIndices function
		/// </summary>
		private bool ILeft(ref SimplifiedVertex a, ref SimplifiedVertex b, ref SimplifiedVertex c)
		{
			return (b.X - a.X) * (c.Z - a.Z)
				 - (c.X - a.X) * (b.Z - a.Z) <= 0;
		}

		private void MergeContours(Contour contA, Contour contB, int ia, int ib)
		{
			int lengthA = contA.Vertices.Length;
			int lengthB = contB.Vertices.Length;

			//create a list with the capacity set to the max number of possible verts to avoid expanding the list.
			List<SimplifiedVertex> newVerts = new List<SimplifiedVertex>(contA.Vertices.Length + contB.Vertices.Length + 2);

			//copy contour A
			for (int i = 0; i <= lengthA; i++)
				newVerts.Add(contA.Vertices[(ia + i) % lengthA]);

			//add contour B (other contour) to contour A (this contour)
			for (int i = 0; i <= lengthB; i++)
				newVerts.Add(contB.Vertices[(ib + i) % lengthB]);

			contA.Vertices = newVerts.ToArray();
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct RawVertex
		{
			public int X;
			public int Y;
			public int Z;
			public int RegionId;

			public RawVertex(int x, int y, int z, int region)
			{
				this.X = x;
				this.Y = y;
				this.Z = z;
				this.RegionId = region;
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct SimplifiedVertex
		{
			public int X;
			public int Y;
			public int Z;
			public int RawVertexIndex;

			public SimplifiedVertex(int x, int y, int z, int rawVertex)
			{
				this.X = x;
				this.Y = y;
				this.Z = z;
				this.RawVertexIndex = rawVertex;
			}

			public SimplifiedVertex(RawVertex rawVert, int index)
			{
				this.X = rawVert.X;
				this.Y = rawVert.Y;
				this.Z = rawVert.Z;
				this.RawVertexIndex = index;
			}
		}
	}
}
