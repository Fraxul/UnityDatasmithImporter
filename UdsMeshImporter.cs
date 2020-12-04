#if UNITY_EDITOR // Importer scripts only work in the Editor

using UnityEngine;
using UnityEditor.Experimental.AssetImporters;
using System;
using System.IO;
using System.Collections.Generic;


[ScriptedImporter(1, "udsmesh")]
public class UdsMeshImporter : ScriptedImporter {

  [Tooltip("Force lightmap UV2 generation during import on large/complex meshes. Note that this can take a significant amount of CPU time.")]
  public bool m_ForceLightmapUVGeneration = false;

  private static int memcmp(byte[] left, byte[] right, uint length) {
    for (int i = 0; i < length; ++i) {
      if (left[i] < right[i])
        return -1;
      else if (left[i] > right[i])
        return 1;
    }
    return 0;
  }

  public static Hash128 NUVHash(Vector3 n, Vector2 uv) {
    Hash128 res = new Hash128();
    HashUtilities.QuantisedVectorHash(ref n, ref res);
    Hash128 uvHash = new Hash128();
    Vector3 uv3 = new Vector3(uv.x, uv.y, 0.0f);
    HashUtilities.QuantisedVectorHash(ref uv3, ref uvHash);
    HashUtilities.AppendHash(ref uvHash, ref res);
    return res;
  }

  public override void OnImportAsset(AssetImportContext ctx) {
    var mesh = new Mesh();
    ctx.AddObjectToAsset("mesh0", mesh);
    ctx.SetMainObject(mesh);

    using (BinaryReader reader = new BinaryReader(File.Open(ctx.assetPath, FileMode.Open))) {


      byte[] searchBuf = new byte[32];
      var markerStr = "DatasmithMeshSourceModel";
      
      byte[] marker = System.Text.Encoding.ASCII.GetBytes(markerStr);
      uint markerLength = (uint) markerStr.Length;

      bool didFindMarker = false;
      while (reader.BaseStream.Position < (reader.BaseStream.Length - markerLength)) {
       
        reader.BaseStream.Read(searchBuf, 0, (int) markerLength);
        if (0 == memcmp(searchBuf, marker, markerLength)) {
          reader.BaseStream.Position += 2; // Skip 2 extra null bytes after the marker
          didFindMarker = true;
          break;
        }

        // rewind to 1 byte after the previous read position
        reader.BaseStream.Position -= (markerLength - 1);     
      }

      if (!didFindMarker) {
        throw new Exception("Couldn't find marker " + markerStr + " in file " + ctx.assetPath);
      }

      for (uint i = 0; i < 6; ++i) {
        if (reader.ReadUInt32() != 0) {
          Console.Out.WriteLine("Warning: expected all zeros between marker and start of material index array");
        }
      }

      reader.ReadUInt32(); // length1
      reader.ReadUInt32(); // length2
      reader.ReadUInt32(); // unknown 9c 00 00 00
      reader.ReadUInt32(); // unknown 00 00 00 00
      reader.ReadUInt32(); // unknown 01 00 00 00
      reader.ReadUInt32(); // unknown 00 00 00 00


      uint materialIndexCount = reader.ReadUInt32();
      uint[] materialIndices = new uint[materialIndexCount];
      for (uint i = 0; i < materialIndexCount; ++i) {
        materialIndices[i] = reader.ReadUInt32();
      }

      uint unknownCount = reader.ReadUInt32();
      uint[] unknownData = new uint[unknownCount];
      for (uint i = 0; i < unknownCount; ++i) {
        unknownData[i] = reader.ReadUInt32();
      }

      List<Vector3> vertices = new List<Vector3>();

      Dictionary<int, int> indexRemap = new Dictionary<int, int>();
      {
        // Collapse vertices and generate an index remapping table
        Dictionary<Vector3, int> uniqueVertices = new Dictionary<Vector3, int>();


        int fileVertexCount = (int) reader.ReadUInt32();
        int vertexLimit = 524288;

        if (fileVertexCount > vertexLimit) {
          ctx.LogImportError(String.Format("UdsMeshImporter: Sanity check failed: File {0} has too many vertices ({1}, limit is {2}) -- returning empty mesh.", ctx.assetPath, fileVertexCount, vertexLimit));
          return;
        }

        for (int i = 0; i < fileVertexCount; ++i) {
          Vector3 v = new Vector3();
          // Adjust scale from cm -> meters
          v.x = reader.ReadSingle() * 0.01f;
          v.y = reader.ReadSingle() * 0.01f;
          v.z = reader.ReadSingle() * 0.01f;

          if (!uniqueVertices.ContainsKey(v)) {
            vertices.Add(v);
            uniqueVertices.Add(v, vertices.Count - 1);
          }

          indexRemap.Add(i, uniqueVertices[v]);
        }
        /*
        if (vertices.Count < fileVertexCount) {
          Debug.Log(String.Format("Vertex position remapping removed {0} nonunique positions", fileVertexCount - vertices.Count));
        }
        */
      }


      uint indexCount = reader.ReadUInt32();
      int[] triangleIndices = new int[indexCount];
      for (uint triIdx = 0; triIdx < (indexCount/3); ++triIdx) {
        triangleIndices[(triIdx * 3) + 0] = indexRemap[(int)reader.ReadUInt32()];
        triangleIndices[(triIdx * 3) + 1] = indexRemap[(int)reader.ReadUInt32()];
        triangleIndices[(triIdx * 3) + 2] = indexRemap[(int)reader.ReadUInt32()];
      }

      reader.ReadUInt32(); // unknown-zero, maybe a count of an unused field
      reader.ReadUInt32(); // unknown-zero, maybe a count of an unused field
      uint normalCount = reader.ReadUInt32();
      Vector3[] normals = new Vector3[normalCount];
      for (uint i = 0; i < normalCount; ++i) {
        normals[i].x = reader.ReadSingle();
        normals[i].y = reader.ReadSingle();
        normals[i].z = reader.ReadSingle();
      }


      uint uvCount = reader.ReadUInt32();
      Vector2[] uvs = new Vector2[uvCount];
      for (uint i = 0; i < uvCount; ++i) {
        uvs[i].x = reader.ReadSingle();
        uvs[i].y = reader.ReadSingle();
      }

      // Datasmith hands us per-face-vertex normals and UVs, which Unity can't handle.
      // Use the Datasmith-supplied index array to write new per-submesh (material group) position/normal/UV buffers.

      {
        var materialToSubmesh = new SortedDictionary<uint, uint>();
        uint submeshCount = 0;
        for (uint triIdx = 0; triIdx < materialIndexCount; ++triIdx) {
          uint midx = materialIndices[triIdx];
          if (!materialToSubmesh.ContainsKey(midx)) {
            materialToSubmesh[midx] = submeshCount;
            submeshCount += 1;
          }
        }


        List<Vector3> cookedPositions = new List<Vector3>();
        List<Vector2> cookedUVs = new List<Vector2>();
        List<Vector3> cookedNormals = new List<Vector3>();
        List<List<int> > cookedSubmeshIndices = new List<List<int> >();

        List<Dictionary<Hash128, int>> vertexCollapseData = new List<Dictionary<Hash128, int>>(vertices.Count);
        // Prepopulate the vertex-collapse list with empty dicts
        for (int vIdx = 0; vIdx < vertices.Count; ++vIdx) {
          vertexCollapseData.Add(new Dictionary<Hash128, int>());
        }
               

        for (uint submeshIndex = 0; submeshIndex < submeshCount; ++submeshIndex) {
          List<int> thisSubmeshIndices = new List<int>();

          for (uint triIdx = 0; triIdx < materialIndexCount; ++triIdx) {

            if (materialToSubmesh[materialIndices[triIdx]] != submeshIndex)
              continue; // this triangle is not relevant in this submesh.

            for (uint triVIdx = 0; triVIdx < 3; ++triVIdx) {
              uint triVIdx_adj = 2 - triVIdx; // Adjusted to swap winding order

              int positionIndex = triangleIndices[(triIdx * 3) + triVIdx_adj];
              Vector3 fvP = vertices[positionIndex];
              Vector2 fvUV = uvs[(triIdx * 3) + triVIdx_adj];
              Vector3 fvN = normals[(triIdx * 3) + triVIdx_adj];

              // Try and find an existing vertex/normal/UV set to reuse
              // We already collapsed coincident positions while reading the vertex and index buffers, so we can partition our search by position index.
              Dictionary<Hash128, int> collapseData = vertexCollapseData[positionIndex];
              Hash128 targetHash = NUVHash(fvN, fvUV);
              int targetVIdx;

              if (collapseData.ContainsKey(targetHash)) {
                // Match found, reuse the previous vertex
                targetVIdx = collapseData[targetHash];
              } else {
                // No match found, so we add it
                cookedPositions.Add(fvP);
                cookedUVs.Add(fvUV);
                cookedNormals.Add(fvN);

                targetVIdx = cookedPositions.Count - 1;
                collapseData.Add(targetHash, targetVIdx);

              }


              thisSubmeshIndices.Add(targetVIdx);
            }

          }
          cookedSubmeshIndices.Add(thisSubmeshIndices);
        }

        mesh.Clear();
        if (cookedPositions.Count > 65535) {
          ctx.LogImportWarning(String.Format("Mesh \"{0}\" has more than 65535 vertices ({1}) and requires a 32-bit index buffer. This mesh may not render correctly on all platforms.", ctx.assetPath, cookedPositions.Count));
          mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }

        mesh.SetVertices(cookedPositions);
        mesh.SetUVs(0, cookedUVs);
        mesh.SetNormals(cookedNormals);
        mesh.subMeshCount = (int) submeshCount;
        for (uint submeshIndex = 0; submeshIndex < submeshCount; ++submeshIndex) {
          mesh.SetIndices(cookedSubmeshIndices[(int) submeshIndex].ToArray(), MeshTopology.Triangles, (int) submeshIndex);
        }

        // Generate lightmap UVs
        if (materialIndexCount > 50000 /*triangles*/) {
          if (m_ForceLightmapUVGeneration) {
            UnityEditor.Unwrapping.GenerateSecondaryUVSet(mesh);
          } else {
            ctx.LogImportWarning(String.Format("Mesh \"{0}\": lightmap UVs won't automatically be generated due to complexity limits. Turn on \"Force Lightmap UV generation\" to override.", ctx.assetPath));
          }
        }

        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
      }

    }
  }
}

#endif