#if UNITY_EDITOR // Importer scripts only work in the Editor

#define USE_VRC_SDK3 // Enable VRChat SDK3 integrations

using UnityEngine;
using UnityEditor;
using UnityEditor.Experimental.AssetImporters;
using System;
using System.IO;
using System.Xml;
using System.Collections.Generic;
using System.Text.RegularExpressions;


[ScriptedImporter(1, "udatasmith")]
public class UDatasmithImporter : ScriptedImporter {

  [Tooltip("Height offset for elements in the 'Ceiling' category; may be used to eliminate Z-fighting of coincident ceiling and floor surfaces. Should probably be negative (move ceiling down)")]
  public float m_CeilingHeightOffset = -0.001f;
  [Tooltip("Height offset for elements in the 'Floor' category; may be used to eliminate Z-fighting of coincident ceiling and floor surfaces. Should probably be positive (move floor up)")]
  public float m_FloorHeightOffset = 0.0f;
  [Tooltip("Import lights. Not all Light types and properties are supported.")]
  public bool m_ImportLights = true;
  [Tooltip("Set up translated lights for static lightmap baking")]
  public bool m_SetupLightmapBaking = true;
  [Tooltip("Set up VRChat networked physics props for translated actors based on Revit Layer attribute")]
  public bool m_SetupPhysicsProps = false;

  [Tooltip("Revit Layers to be mapped as static objects with Mesh colliders. (Regular expression, case-insensitive)")]
  public string m_StaticTangibleLayerRegex = @"Roof|Topography|Furniture|Stairs|Levels|Walls|Structural|Floors|Site|Pads|Ramps|Ceilings|Casework|Parking|Parts|Pipes|Pipe Fitting|Gutters|Fascia";
  [Tooltip("Revit Layers to be mapped as static, intangible (no collision) objects. (Regular expression, case-insensitive)")]
  public string m_StaticIntangibleLayerRegex = @"Windows|Doors|Specialty Equipment|Plumbing|Electrical|Curtain|Railings|Top Rails|Lighting Fixtures|Mechanical|Equipment";
  [Tooltip("Revit Layers to be mapped as physics props. (Regular expression, case-insensitive)")]
  public string m_PhysicsPropsLayerRegex = @"Generic Models";
  [Tooltip("Revit Layers to import as disabled GameObjects. (Regular expression, case-insensitive)")]
  public string m_IgnoredLayerRegex = @"Entourage|Planting";


  class UdsStaticMesh {
    UdsStaticMesh() {
      materialNames = new List<String>();
      materialRefs = new List<UdsMaterial>();
    }

    public String name;
    public String filePath;
    public List<String> materialNames;
    public List<UdsMaterial> materialRefs;
    public Mesh assetRef;

    public static UdsStaticMesh FromNode(AssetImportContext ctx, XmlNode node) {
      Debug.Assert(node.Name == "StaticMesh");
      UdsStaticMesh m = new UdsStaticMesh();

      m.name = node.Attributes["name"].Value;

      SortedDictionary<int, String> materialIdtoName = new SortedDictionary<int, string>();
      foreach (XmlNode cn in node.ChildNodes) {
        if (cn.Name == "file") {
          m.filePath = cn.Attributes["path"].Value;
        } else if (cn.Name == "Material") {
          materialIdtoName.Add(Int32.Parse(cn.Attributes["id"].Value), cn.Attributes["name"].Value);
        }
      }

      foreach (var iter in materialIdtoName) {
        // We use sorted material IDs for submesh mapping, same as the udsmesh importer
        m.materialNames.Add(iter.Value);
      }

      //Debug.Log(String.Format("StaticMesh: \"{0}\" => file \"{1}\", {2} materials", m.name, m.filePath, m.materialNames.Count));
      String fullMeshPath = Path.Combine(Path.GetDirectoryName(ctx.assetPath), m.filePath);
      m.assetRef = (Mesh)AssetDatabase.LoadAssetAtPath(fullMeshPath, typeof(Mesh));
      if (!m.assetRef) {
        Debug.Log(String.Format("StaticMesh: AssetDatabase.LoadAssetAtPath(\"{0}\") failed to return a mesh object", fullMeshPath));
      }


      return m;
    }
  };

  class UdsTexture {
    public String name;
    public String filePath;
    public String fullyQualifiedPath;
    public Texture assetRef;
    public TextureImporter importer;
    public static UdsTexture FromNode(AssetImportContext ctx, XmlNode node) {
      UdsTexture tex = new UdsTexture();
      /*
       * <Texture name="concrete_cast-in-place_formwork_wood_boards_bump_0" texturemode="0" texturefilter="3" textureaddressx="0" textureaddressy="0" rgbcurve="-1.000000"
       *          srgb="0" file="rac_basic_sample_project_local_copy-3DView-UE4_Assets/concrete.cast-in-place.formwork.wood.boards.bump.jpg">
		   *   <Hash value="c99e25a6f94199ce085a6e78e56639f2"/>
	     * </Texture>
      */

      tex.name = node.Attributes["name"].Value;
      tex.filePath = node.Attributes["file"].Value;

      if (Regex.Match(tex.filePath, @"\.ies$", RegexOptions.IgnoreCase).Success) {
        ctx.LogImportWarning(String.Format("Texture Reference \"{0}\" to IES light profile \"{1}\" cannot be resolved: IES light profile import is not implemented.", tex.name, tex.filePath));
        return null;
      }


      tex.fullyQualifiedPath = Path.Combine(Path.GetDirectoryName(ctx.assetPath), tex.filePath);

      var texAssetObj = AssetDatabase.LoadAssetAtPath(tex.fullyQualifiedPath, typeof(Texture));
      if (texAssetObj != null) {
        tex.assetRef = (Texture)texAssetObj;
        var texImporterObj = AssetImporter.GetAtPath(tex.fullyQualifiedPath); // load import settings for possible later adjustment once we know what this will be used for
        if (texImporterObj != null) {
          tex.importer = (TextureImporter)texImporterObj;
        }
      }

      if (tex.assetRef == null || tex.importer == null) {
        ctx.LogImportError(String.Format("UdsTexture::FromNode: Asset does not exist at path \"{0}\"", tex.fullyQualifiedPath));
      }

      return tex;
    }
  };

  class UdsMaterial {

    public String name;
    public Material assetRef;

    public static Color ParseKVPColor(XmlNode node) {
      Debug.Assert(node.Attributes["type"].Value == "Color");
      float r, g, b, a;

      Match m = Regex.Match(node.Attributes["val"].Value, @"[rR]\s*=\s*([0-9.+-]+)\s*,\s*[gG]\s*=\s*([0-9.+-]+)\s*,\s*[bB]\s*=\s*([0-9.+-]+)\s*,\s*[aA]\s*=\s*([0-9.+-]+)");
      Debug.Assert(m.Success);
      r = Single.Parse(m.Groups[1].ToString());
      g = Single.Parse(m.Groups[2].ToString());
      b = Single.Parse(m.Groups[3].ToString());
      a = Single.Parse(m.Groups[4].ToString());

      return new Color(r, g, b, a);
    }

    public static void FillUVOffsetScaleParameters(XmlNode node, String attrBasename, out Vector2 uvOffset, out Vector2 uvScale) {

      uvOffset = new Vector2(0.0f, 0.0f);
      uvScale = new Vector2(1.0f, 1.0f);

      XmlNode pn;
      pn = node.SelectSingleNode("child::KeyValueProperty[@name='" + attrBasename + "_UVOffsetX']");
      if (pn != null) uvOffset.x = Single.Parse(pn.Attributes["val"].Value);
      pn = node.SelectSingleNode("child::KeyValueProperty[@name='" + attrBasename + "_UVOffsetY']");
      if (pn != null) uvOffset.y = Single.Parse(pn.Attributes["val"].Value);

      pn = node.SelectSingleNode("child::KeyValueProperty[@name='" + attrBasename + "_UVScaleX']");
      if (pn != null) uvScale.x = Single.Parse(pn.Attributes["val"].Value);
      pn = node.SelectSingleNode("child::KeyValueProperty[@name='" + attrBasename + "_UVScaleY']");
      if (pn != null) uvScale.y = Single.Parse(pn.Attributes["val"].Value);
    }

    public static UdsMaterial FromNode(AssetImportContext ctx, XmlNode node, Dictionary<String, UdsTexture> textureElements) {
      Debug.Assert(node.Name == "MasterMaterial");
      UdsMaterial m = new UdsMaterial();

      m.assetRef = new Material(Shader.Find("Standard"));
      m.assetRef.name = node.Attributes["label"].Value;
      m.name = node.Attributes["name"].Value;

      /*
       * 		<KeyValueProperty name="DiffuseColor" type="Color" val="(R=0.784314,G=0.619608,B=0.243137,A=1.000000)"/>
            <KeyValueProperty name="DiffuseMapFading" type="Float" val="0.350000"/>                                                       // mix between DiffuseColor and DiffuseMap?
            <KeyValueProperty name="DiffuseMap" type="Texture" val="woods_and_plastics_finish_carpentry_wood_paneling_1_0"/>
            <KeyValueProperty name="DiffuseMap_UVOffsetX" type="Float" val="0.000000"/>
            <KeyValueProperty name="DiffuseMap_UVOffsetY" type="Float" val="0.000000"/>
            <KeyValueProperty name="DiffuseMap_UVScaleX" type="Float" val="0.169333"/>
            <KeyValueProperty name="DiffuseMap_UVScaleY" type="Float" val="0.169333"/>
            <KeyValueProperty name="DiffuseMap_UVWAngle" type="Float" val="0.000000"/>
            <KeyValueProperty name="TintEnabled" type="Bool" val="True"/>
            <KeyValueProperty name="TintColor" type="Color" val="(R=0.172549,G=0.172549,B=0.172549,A=1.000000)"/>
            <KeyValueProperty name="SelfIlluminationLuminance" type="Float" val="0.000000"/>
            <KeyValueProperty name="SelfIlluminationFilter" type="Color" val="(R=1.000000,G=1.000000,B=1.000000,A=1.000000)"/>
            <KeyValueProperty name="SelfIlluminationMapEnable" type="Bool" val="False"/>
            <KeyValueProperty name="BumpAmount" type="Float" val="9.310000"/>
            <KeyValueProperty name="BumpMap" type="Texture" val="woods_and_plastics_finish_carpentry_wood_paneling_1_bump_0"/>
            <KeyValueProperty name="BumpMap_UVOffsetX" type="Float" val="0.000000"/>
            <KeyValueProperty name="BumpMap_UVOffsetY" type="Float" val="0.000000"/>
            <KeyValueProperty name="BumpMap_UVScaleX" type="Float" val="0.169333"/>
            <KeyValueProperty name="BumpMap_UVScaleY" type="Float" val="0.169333"/>
            <KeyValueProperty name="BumpMap_UVWAngle" type="Float" val="0.000000"/>
            <KeyValueProperty name="IsMetal" type="Bool" val="False"/>
            <KeyValueProperty name="Glossiness" type="Float" val="0.000000"/>

        if (Type == 2) {
          <KeyValueProperty name="Transparency" type="Float" val="0.850000"/>
      		<KeyValueProperty name="TransparencyMapFading" type="Float" val="0.000000"/>
      		<KeyValueProperty name="RefractionIndex" type="Float" val="1.010000"/>
        }

      */

      foreach (XmlNode kvpNode in node.ChildNodes) {
        if (kvpNode.Name != "KeyValueProperty") continue;
        String keyName = kvpNode.Attributes["name"].Value;
        if (keyName == "DiffuseColor") {
          Color kvpColor = ParseKVPColor(kvpNode);
          m.assetRef.color = new Color(kvpColor.r, kvpColor.g, kvpColor.b, m.assetRef.color.a); // Alpha channel is preserved since we set it from the "Transparency" key

        } else if (keyName == "DiffuseMap") {
          Debug.Assert(kvpNode.Attributes["type"].Value == "Texture");
          UdsTexture texRef;
          textureElements.TryGetValue(kvpNode.Attributes["val"].Value, out texRef);
          if (texRef == null) {
            ctx.LogImportError(String.Format("Missing diffuse texref \"{0}\" while assembling material node \"{1}\"", kvpNode.Attributes["val"].Value, m.name));
          } else {
            Vector2 uvOffset, uvScale;
            FillUVOffsetScaleParameters(node, "DiffuseMap", out uvOffset, out uvScale);

            m.assetRef.SetTexture("_MainTex", texRef.assetRef);
            m.assetRef.SetTextureOffset("_MainTex", uvOffset);
            m.assetRef.SetTextureScale("_MainTex", uvScale);
          }
        } else if (keyName == "BumpMap") {
          Debug.Assert(kvpNode.Attributes["type"].Value == "Texture");
          UdsTexture texRef;
          textureElements.TryGetValue(kvpNode.Attributes["val"].Value, out texRef);
          if (texRef == null) {
            ctx.LogImportError(String.Format("Missing bump texref \"{0}\" while assembling material node \"{1}\"", kvpNode.Attributes["val"].Value, m.name));
          } else {
            Vector2 uvOffset, uvScale;
            FillUVOffsetScaleParameters(node, "BumpMap", out uvOffset, out uvScale);

            if (!texRef.importer.convertToNormalmap) {
              // Update importer config for this texture to convert it to a normal map
              texRef.importer.textureType = TextureImporterType.NormalMap;
              texRef.importer.convertToNormalmap = true;
              texRef.importer.SaveAndReimport();
            }

            m.assetRef.SetTexture("_BumpMap", texRef.assetRef);
            m.assetRef.SetTextureOffset("_BumpMap", uvOffset);
            m.assetRef.SetTextureScale("_BumpMap", uvScale);
            m.assetRef.EnableKeyword("_NORMALMAP"); // ref: https://docs.unity3d.com/Manual/materials-scripting-standard-shader.html
          }
        } else if (keyName == "BumpAmount") {
          m.assetRef.SetFloat("_BumpScale", Math.Min(Single.Parse(kvpNode.Attributes["val"].Value), 1.0f));
        } else if (keyName == "Transparency") {
          float transparency = Single.Parse(kvpNode.Attributes["val"].Value);
          if (transparency > 0.0f) {

            // Set up the material for the "Transparent" transparency mode.
            // This code is borrowed from StandardShaderGUI.cs:365 in the Unity 2018.4.20 builtin shaders package
            m.assetRef.SetInt("_Mode", 3); // StandardShader.BlendMode enum: {Opaque=0, Cutout=1, Fade=2, Transparent=3}
            m.assetRef.SetOverrideTag("RenderType", "Transparent");
            m.assetRef.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            m.assetRef.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.assetRef.SetInt("_ZWrite", 0);
            m.assetRef.DisableKeyword("_ALPHATEST_ON");
            m.assetRef.DisableKeyword("_ALPHABLEND_ON");
            m.assetRef.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            m.assetRef.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            // End borrowed setup code
            
            Color c = m.assetRef.color;
            c.a = 1.0f - transparency;
            m.assetRef.color = c;
            
          }
        } else if (keyName == "IsMetal") {
          m.assetRef.SetFloat("_Metallic", Boolean.Parse(kvpNode.Attributes["val"].Value) ? 1.0f : 0.0f);
        } else if (keyName == "Glossiness") {
          m.assetRef.SetFloat("_Glossiness", Single.Parse(kvpNode.Attributes["val"].Value));
        } else if (keyName == "SelfIlluminationLuminance") {
            float emissiveLuma = Single.Parse(kvpNode.Attributes["val"].Value);
            if (emissiveLuma > 0.0f) {
              // Emissive surfaces seem to mostly be used for light fixture glass. Those components also have lights attached,
              // so we don't need the emissive component to contribute to global illumination.
              m.assetRef.EnableKeyword("_EMISSION");
              m.assetRef.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
              m.assetRef.SetColor("_EmissionColor", new Color(emissiveLuma, emissiveLuma, emissiveLuma, emissiveLuma));
            }
        }


      } // Kvp node loop

      ctx.AddObjectToAsset(m.name, m.assetRef);

      return m;
    }
  };

  private Dictionary<String, UdsStaticMesh> staticMeshElements;
  private Dictionary<String, UdsMaterial> materialElements;
  private Dictionary<String, UdsTexture> textureElements;
  private Dictionary<String, Dictionary<String, String> > actorMetadata;

  private float clamp(float x, float a, float b) {
    return Math.Min(Math.Max(x, a), b);
  }

  private void ImportActorChildren(AssetImportContext ctx, GameObject parentObject, XmlNode containerNode) {

    foreach (XmlNode node in containerNode.ChildNodes) {
      if (!(node.Name == "Actor" || node.Name == "ActorMesh" || node.Name == "Light"))
        continue; // Only examine supported nodes

      String objName = node.Attributes["label"].Value + "_" + node.Attributes["name"].Value;
      GameObject obj = new GameObject(objName);
      obj.transform.SetParent(parentObject.transform, /*worldPositionStays=*/false);
      //Do NOT call ctx.AddObjectToAsset on these GameObjects. It should only be called on the root GameObject of the hierarchy (which the Unity manual fails to mention, of course.)
      // Calling ctx.AddObjectToAsset on the child GameObjects will cause Unity to crash when changing importer settings.

      {
        XmlNode xfNode = node.SelectSingleNode("child::Transform");
        if (xfNode != null) {
          // Transform position and rotation are stored in an absolute coordinate space. TODO: not sure if Scale will be applied correctly
          // Datasmith units are cm, while Unity units are m; we adjust the scale of mesh vertices and of incoming Transform nodes to match.

          obj.transform.position = new Vector3(
            Single.Parse(xfNode.Attributes["tx"].Value) * 0.01f,
            Single.Parse(xfNode.Attributes["ty"].Value) * 0.01f,
            Single.Parse(xfNode.Attributes["tz"].Value) * 0.01f);
          obj.transform.localScale = new Vector3(
            Single.Parse(xfNode.Attributes["sx"].Value),
            Single.Parse(xfNode.Attributes["sy"].Value),
            Single.Parse(xfNode.Attributes["sz"].Value));
          obj.transform.rotation = new Quaternion(
            Single.Parse(xfNode.Attributes["qx"].Value),
            Single.Parse(xfNode.Attributes["qy"].Value),
            Single.Parse(xfNode.Attributes["qz"].Value),
            Single.Parse(xfNode.Attributes["qw"].Value));
        }
      } // transform processing

      if (node.Name == "ActorMesh") {
        XmlNode meshNode = node.SelectSingleNode("child::mesh");
        if (meshNode != null) {
          String meshName = meshNode.Attributes["name"].Value;
          UdsStaticMesh mesh;

          staticMeshElements.TryGetValue(meshName, out mesh);
          Debug.Assert(mesh != null, String.Format("Missing StaticMesh node for \"{0}\" referenced from ActorMesh node \"{1}\"", meshName, node.Attributes["name"].Value));
          if (mesh.assetRef) {
            MeshFilter mf = obj.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh.assetRef;

            Material[] mats = new Material[mesh.assetRef.subMeshCount];
            for (int materialIdx = 0; materialIdx < mesh.assetRef.subMeshCount; ++materialIdx) {
              mats[materialIdx] = mesh.materialRefs[materialIdx].assetRef;
            }

            MeshRenderer mr = obj.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;

            String RevitLayer = node.Attributes["layer"].Value;
            //Dictionary<String, String> metadata = new Dictionary<string, string>();
            //actorMetadata.TryGetValue(node.Attributes["name"].Value, out metadata);

            // Process imported metadata and try to do something reasonable with it
            {
              //String RevitCategory;
              //metadata.TryGetValue("Element_Category", out RevitCategory);
              if (RevitLayer != null) {

                if (Regex.Match(RevitLayer, @"Ceilings", RegexOptions.IgnoreCase).Success) {
                  // Apply ceiling height offset
                  Vector3 p = obj.transform.position;
                  p.z += m_CeilingHeightOffset;
                  obj.transform.position = p;
                }

                if (Regex.Match(RevitLayer, @"Floors", RegexOptions.IgnoreCase).Success) {
                  // Apply floor height offset
                  Vector3 p = obj.transform.position;
                  p.z += m_FloorHeightOffset;
                  obj.transform.position = p;
                }


                if (Regex.Match(RevitLayer, m_IgnoredLayerRegex, RegexOptions.IgnoreCase).Success) {
                  // Default-hidden objects. For example, "Entourage" and "Planting" objects are not exported correctly by Datasmith (no materials/textures), so we hide them.
                  obj.SetActive(false);
                } else if (Regex.Match(RevitLayer, m_StaticTangibleLayerRegex, RegexOptions.IgnoreCase).Success) {
                  // Completely static objects that should be lightmapped and have collision enabled
                  GameObjectUtility.SetStaticEditorFlags(obj, StaticEditorFlags.LightmapStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.BatchingStatic | StaticEditorFlags.ReflectionProbeStatic);

                  // Collision
                  MeshCollider collider = obj.AddComponent<MeshCollider>();
                  collider.cookingOptions = (MeshColliderCookingOptions.CookForFasterSimulation | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices);

                } else if (Regex.Match(RevitLayer, m_StaticIntangibleLayerRegex, RegexOptions.IgnoreCase).Success) {
                  // Completely static objects that should be lightmapped, but don't need collision
                  GameObjectUtility.SetStaticEditorFlags(obj, StaticEditorFlags.LightmapStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.BatchingStatic | StaticEditorFlags.ReflectionProbeStatic);

                } else if (Regex.Match(RevitLayer, m_PhysicsPropsLayerRegex).Success) {
                  // Clutter that can be physics-enabled

                  MeshCollider collider = obj.AddComponent<MeshCollider>();
                  collider.cookingOptions = (MeshColliderCookingOptions.CookForFasterSimulation | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices);

#if USE_VRC_SDK3
                  if (m_SetupPhysicsProps) {

                    collider.convex = true;

                    Rigidbody rb = obj.AddComponent<Rigidbody>();
                    // rb.collisionDetectionMode = CollisionDetectionMode.Continuous; // Higher quality collision detection, but slower.

                    // Add VRCPickup component to make the object interactable
                    VRC.SDK3.Components.VRCPickup pickup = obj.AddComponent<VRC.SDK3.Components.VRCPickup>();

                    pickup.pickupable = true;
                    pickup.allowManipulationWhenEquipped = true;

                    // Add UdonBehaviour component to replicate the object's position
                    VRC.Udon.UdonBehaviour udon = obj.AddComponent<VRC.Udon.UdonBehaviour>();
                    udon.SynchronizePosition = true;

                    // TODO see if it's possible to only enable gravity on objects the first time they're picked up (so wall/ceiling fixtures can remain in place until grabbed)
                  }

#endif


                } else {
                  ctx.LogImportWarning(String.Format("Unhandled Layer \"{0}\" -- ActorMesh \"{1}\" will not have physics and lighting behaviours automatically mapped", RevitLayer, objName));
                }

                if (Regex.Match(RevitLayer, @"Lighting").Success) {
                  // Turn off shadow casting on light fixtures. Light sources are usually placed inside the fixture body and we don't want the fixture geometry to block them.
                  mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

              }
            }
            



          } else {
            ctx.LogImportError(String.Format("ActorMesh {0} mesh {1} assetRef is NULL", obj.name, meshName));
          }
        }
      } // ActorMesh

      if (node.Name == "Light" && m_ImportLights) {
        Light light = obj.AddComponent<Light>();
        if (node.Attributes["type"].Value == "PointLight") {
          light.type = LightType.Point;
        } else {
          ctx.LogImportWarning(String.Format("Light {0}: Unhandled \"type\" \"{1}\", defaulting to Point", objName, node.Attributes["type"].Value));
        }

          { // Color temperature or RGB color
          XmlNode colorNode = node.SelectSingleNode("child::Color");
          if (colorNode != null) {
            if (Int32.Parse(colorNode.Attributes["usetemp"].Value) != 0) {
              float colorTemperature = Single.Parse(colorNode.Attributes["temperature"].Value);
              // There doesn't appear to be a way to turn on color temperature mode on the Light programmatically (why?)
              // Convert it to RGB; algorithm borrowed from https://tannerhelland.com/2012/09/18/convert-temperature-rgb-algorithm-code.html

              float tmpKelvin = Mathf.Clamp(colorTemperature, 1000.0f, 40000.0f) / 100.0f;
              // Note: The R-squared values for each approximation follow each calculation
              float r = tmpKelvin <= 66.0f ? 255.0f :
                Mathf.Clamp(329.698727446f * (Mathf.Pow(tmpKelvin - 60.0f, -0.1332047592f)), 0.0f, 255.0f);  // .988

              float g = tmpKelvin <= 66 ?
                Mathf.Clamp(99.4708025861f * Mathf.Log(tmpKelvin) - 161.1195681661f, 0.0f, 255.0f) :      // .996
                Mathf.Clamp(288.1221695283f * (Mathf.Pow(tmpKelvin - 60.0f, -0.0755148492f)), 0.0f, 255.0f); // .987

              float b = tmpKelvin >= 66 ? 255 :
                tmpKelvin <= 19 ? 0 :
                Mathf.Clamp(138.5177312231f * Mathf.Log(tmpKelvin - 10.0f) - 305.0447927307f, 0.0f, 255.0f);  // .998

              light.color = new Color(r/255.0f, g/255.0f, b/255.0f);
            } else {
              float r = Single.Parse(colorNode.Attributes["R"].Value);
              float g = Single.Parse(colorNode.Attributes["G"].Value);
              float b = Single.Parse(colorNode.Attributes["B"].Value);
              light.color = new Color(r, g, b);
            }
          }
        }

        // Common light parameters
        if (m_SetupLightmapBaking) {
          light.lightmapBakeType = LightmapBakeType.Baked;
        }

        GameObjectUtility.SetStaticEditorFlags(obj, StaticEditorFlags.LightmapStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.BatchingStatic | StaticEditorFlags.ReflectionProbeStatic);
      }

      { // children node processing
        XmlNode childrenNode = node.SelectSingleNode("child::children");
        if (childrenNode != null) {
          // TODO obey visible="true" / visible="false" attribute on children node
          ImportActorChildren(ctx, obj, childrenNode);
        }
      } // children node processing

    } // child loop
  }



  public override void OnImportAsset(AssetImportContext ctx) {

    XmlDocument doc = new XmlDocument();
    {
      String wholeDoc = File.ReadAllText(ctx.assetPath);
      // Datasmith doesn't properly escape & in texture filename XML elements, so we need to do that before we can pass the document to XmlDocument or it will throw a parse error.
      doc.LoadXml(System.Text.RegularExpressions.Regex.Replace(wholeDoc, @"&([^;]{8}?)", "&amp;$1"));
    }

    XmlElement rootElement = doc.DocumentElement;
    Debug.Assert(rootElement.Name == "DatasmithUnrealScene");

    staticMeshElements = new Dictionary<string, UdsStaticMesh>();
    materialElements = new Dictionary<string, UdsMaterial>();
    textureElements = new Dictionary<string, UdsTexture>();
    actorMetadata = new Dictionary<String, Dictionary<String, String>>();

    // Populate actor metadata dictionaries
    foreach (XmlNode metadataNode in rootElement.SelectNodes("child::MetaData")) {
      var m = Regex.Match(metadataNode.Attributes["reference"].Value, @"Actor\.(.*)$");
      if (!m.Success) continue;
      String actorId = m.Groups[1].Value;

      Dictionary<String, String> metadata = new Dictionary<string, string>();
      foreach (XmlNode kvpNode in metadataNode.SelectNodes("child::KeyValueProperty")) {
        metadata.Add(kvpNode.Attributes["name"].Value, kvpNode.Attributes["val"].Value);
      }
      actorMetadata.Add(actorId, metadata);
    }

    // Import textures
    foreach (XmlNode node in rootElement.SelectNodes("child::Texture")) {
      UdsTexture tex = UdsTexture.FromNode(ctx, node);
      if (tex != null) {
        textureElements.Add(tex.name, tex);
      }
    }

    // Import materials
    foreach (XmlNode node in rootElement.SelectNodes("child::MasterMaterial")) {
      UdsMaterial m = UdsMaterial.FromNode(ctx, node, textureElements);
      materialElements.Add(m.name, m);
    }

    // Import StaticMesh nodes and crossreference materials
    foreach (XmlNode node in rootElement.SelectNodes("child::StaticMesh")) {
      UdsStaticMesh m = UdsStaticMesh.FromNode(ctx, node);

      for (int materialIdx = 0; materialIdx < m.materialNames.Count; ++materialIdx) {
        UdsMaterial mat;
        if (!materialElements.TryGetValue(m.materialNames[materialIdx], out mat)) {
          ctx.LogImportError(String.Format("Can't resolve Material ref \"{0}\"", m.materialNames[materialIdx]));
        }
        m.materialRefs.Add(mat);
      }

      staticMeshElements.Add(m.name, m);

    }


    GameObject sceneRoot = new GameObject("datasmithRoot");
    sceneRoot.transform.rotation = Quaternion.Euler(90.0f, 0.0f, 0.0f); // Convert Revit's Z-up orientation to Unity's Y-up orientation
    ctx.AddObjectToAsset("datasmithRoot", sceneRoot);
    ctx.SetMainObject(sceneRoot);

    ImportActorChildren(ctx, sceneRoot, rootElement);

    // cleanup (TODO not sure if this is required / what the lifecycle of this object looks like)
    staticMeshElements = null;
    materialElements = null;
    textureElements = null;
    actorMetadata = null;
  }

};

#endif // UNITY_EDITOR