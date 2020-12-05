# UnityDatasmithImporter

### About
This is a set of Unity ScriptedImporter scripts to import Unreal Datasmith bundles into Unity. It's designed to quickly set up VRChat worlds using the output of the Autodesk Revit Datasmith plugin, but could potentially be improved or serve as a starting point for other use cases.

### Quick Start
* Create a new Unity project using the appropriate Unity version for the VRChat SDK (2018.4.20f1 at the time of this writing)
* Import VRChat's World SDK3
* Checkout this repository to your Assets folder (or otherwise copy these scripts into your project)
* Copy a udatasmith file and its associated Data folder into your Assets
* Drop the prefab generated from the udatasmith file into the Scene.
* Import the VRCWorld prefab from the SDK and move it to set up the spawn point
* Bake lightmaps (or turn off "Setup Lightmap Baking" on the udatasmith asset settings)
* Build and test!

### Supported datatypes
* The world transform hierarchy
* StaticMesh actors and material assignments
* Common Material properties (Diffuse and Bump maps)
* Lights -- early WIP, most Light properties are not translated.


