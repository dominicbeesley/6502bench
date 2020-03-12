﻿/*
 * Copyright 2020 faddenSoft
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PluginCommon {
    /// <summary>
    /// Wireframe mesh with optional backface normals, for use with visualization generators.
    /// Call the various functions to add data, then call Validate() to check for broken
    /// references.
    /// </summary>
    [Serializable]
    public class VisWireframe : IVisualizationWireframe {
        public const string P_IS_PERSPECTIVE = "_isPerspective";
        public const string P_IS_BFC_ENABLED = "_isBfcEnabled";

        public static VisParamDescr Param_IsPerspective(string uiLabel, bool defaultVal) {
            return new VisParamDescr(uiLabel, P_IS_PERSPECTIVE, typeof(bool), 0, 0, 0, defaultVal);
        }
        public static VisParamDescr Param_IsBfcEnabled(string uiLabel, bool defaultVal) {
            return new VisParamDescr(uiLabel, P_IS_BFC_ENABLED, typeof(bool), 0, 0, 0, defaultVal);
        }

        private List<float> mVerticesX = new List<float>();
        private List<float> mVerticesY = new List<float>();
        private List<float> mVerticesZ = new List<float>();

        private List<IntPair> mEdges = new List<IntPair>();

        private List<float> mNormalsX = new List<float>();
        private List<float> mNormalsY = new List<float>();
        private List<float> mNormalsZ = new List<float>();

        private List<IntPair> mVertexFaces = new List<IntPair>();
        private List<IntPair> mEdgeFaces = new List<IntPair>();

        /// <summary>
        /// Constructor.  Nothing much to do.
        /// </summary>
        public VisWireframe() { }

        /// <summary>
        /// Adds the vertex to the list.
        /// </summary>
        /// <param name="x">X coordinate.</param>
        /// <param name="y">Y coordinate.</param>
        /// <param name="z">Z coordinate.</param>
        /// <returns>Vertex index.  Indices start at zero and count up.</returns>
        public int AddVertex(float x, float y, float z) {
            mVerticesX.Add(x);
            mVerticesY.Add(y);
            mVerticesZ.Add(z);
            return mVerticesX.Count - 1;
        }

        /// <summary>
        /// Adds an edge to the list.  The referenced vertices do not need to be defined
        /// before calling.
        /// </summary>
        /// <param name="index0">Index of first vertex.</param>
        /// <param name="index1">Index of second vertex.</param>
        /// <returns>Edge index.  Indices start at zero and count up.</returns>
        public int AddEdge(int index0, int index1) {
            Debug.Assert(index0 >= 0);
            Debug.Assert(index1 >= 0);
            mEdges.Add(new IntPair(index0, index1));
            return mEdges.Count - 1;
        }

        /// <summary>
        /// Adds the face normal to the list.
        /// </summary>
        /// <param name="x">X coordinate.</param>
        /// <param name="y">Y coordinate.</param>
        /// <param name="z">Z coordinate.</param>
        /// <returns>Face index.  Indices start at zero and count up.</returns>
        public int AddFaceNormal(float x, float y, float z) {
            Debug.Assert(x != 0.0f || y != 0.0f || z != 0.0f);  // no zero-length normals
            mNormalsX.Add(x);
            mNormalsY.Add(y);
            mNormalsZ.Add(z);
            return mNormalsX.Count - 1;
        }

        /// <summary>
        /// Replaces the specified face normal.
        /// </summary>
        /// <param name="index">Face index.</param>
        /// <param name="x">X coordinate.</param>
        /// <param name="y">Y coordinate.</param>
        /// <param name="z">Z coordinate.</param>
        public void ReplaceFaceNormal(int index, float x, float y, float z) {
            mNormalsX[index] = x;
            mNormalsY[index] = y;
            mNormalsZ[index] = z;
        }

        /// <summary>
        /// Marks a vertex's visibility as being tied to the specified face.  The vertices and
        /// faces being referenced do not need to exist yet.
        /// </summary>
        /// <param name="vertexIndex">Index of vertex.</param>
        /// <param name="faceIndex">Index of face.</param>
        public void AddVertexFace(int vertexIndex, int faceIndex) {
            Debug.Assert(vertexIndex >= 0);
            Debug.Assert(faceIndex >= 0);
            mVertexFaces.Add(new IntPair(vertexIndex, faceIndex));
        }

        /// <summary>
        /// Marks an edge's visibility as being tied to the specified face.  The edges and
        /// faces being referenced do not need to exist yet.
        /// </summary>
        /// <param name="edgeIndex">Index of edge.</param>
        /// <param name="faceIndex">Index of face.</param>
        public void AddEdgeFace(int edgeIndex, int faceIndex) {
            Debug.Assert(edgeIndex >= 0);
            Debug.Assert(faceIndex >= 0);
            mEdgeFaces.Add(new IntPair(edgeIndex, faceIndex));
        }

        /// <summary>
        /// Verifies that the various references by index are valid.
        /// </summary>
        /// <param name="msg">Failure detail.</param>
        /// <returns>True if everything looks valid.</returns>
        public bool Validate(out string msg) {
            int vertexCount = mVerticesX.Count;
            int faceCount = mNormalsX.Count;
            int edgeCount = mEdges.Count;

            // complain about empty objects (should we fail if no edges were defined?)
            if (vertexCount == 0) {
                msg = "no vertices defined";
                return false;
            }

            // check edges
            foreach (IntPair ip in mEdges) {
                if (ip.Val0 < 0 || ip.Val0 >= vertexCount ||
                        ip.Val1 < 0 || ip.Val1 >= vertexCount) {
                    msg = "invalid edge (vertices " + ip.Val0 + ", " + ip.Val1 +
                        "; count=" + vertexCount + ")";
                    return false;
                }
            }

            // check vertex-faces
            foreach (IntPair ip in mVertexFaces) {
                if (ip.Val0 < 0 || ip.Val0 >= vertexCount ||
                        ip.Val1 < 0 || ip.Val1 >= faceCount) {
                    msg = "invalid vertex-face (v=" + ip.Val0 + ", f=" + ip.Val1 + ")";
                    return false;
                }
            }

            // check edge-faces
            foreach (IntPair ip in mVertexFaces) {
                if (ip.Val0 < 0 || ip.Val0 >= edgeCount ||
                        ip.Val1 < 0 || ip.Val1 >= faceCount) {
                    msg = "invalid edge-face (e=" + ip.Val0 + ", f=" + ip.Val1 + ")";
                    return false;
                }
            }

            // check face normals
            for (int i = 0; i < mNormalsX.Count; i++) {
                if (mNormalsX[i] == 0.0f && mNormalsY[i] == 0.0f && mNormalsZ[i] == 0.0f) {
                    msg = "zero-length normal";
                    return false;
                }
            }

            // TODO(maybe): confirm that every face (i.e. normal) has a vertex we can use for
            // BFC calculation.  Not strictly necessary since you can do orthographic-projection
            // BFC without it... but who does that?

            msg = string.Empty;
            return true;
        }

        //
        // IVisualizationWireframe implementation.
        //

        public float[] GetVerticesX() {
            return mVerticesX.ToArray();
        }

        public float[] GetVerticesY() {
            return mVerticesY.ToArray();
        }

        public float[] GetVerticesZ() {
            return mVerticesZ.ToArray();
        }

        public IntPair[] GetEdges() {
            return mEdges.ToArray();
        }

        public float[] GetNormalsX() {
            return mNormalsX.ToArray();
        }

        public float[] GetNormalsY() {
            return mNormalsY.ToArray();
        }

        public float[] GetNormalsZ() {
            return mNormalsZ.ToArray();
        }

        public IntPair[] GetVertexFaces() {
            return mVertexFaces.ToArray();
        }

        public IntPair[] GetEdgeFaces() {
            return mEdgeFaces.ToArray();
        }


        public override string ToString() {
            return "[VisWireframe: " + mVerticesX.Count + " vertices, " +
                mEdges.Count + " edges, " +
                mNormalsX.Count + " faces, " +
                mVertexFaces.Count + " vfaces, " +
                mEdgeFaces.Count + " efaces]";
        }
    }
}