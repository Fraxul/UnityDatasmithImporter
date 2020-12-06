# UnityDatasmithImporter

### About
This is a set of Unity ScriptedImporter scripts to import Unreal Datasmith bundles into Unity. It's designed to quickly set up VRChat worlds using the output of the Autodesk Revit Datasmith plugin, but could potentially be improved or serve as a starting point for other use cases.

### Quick Start (Revit)
* Download and install the Revit Datasmith plugin (version 4.26 at the time of this writing) and its prerequisites package from https://www.unrealengine.com/en-US/datasmith/plugins
* Open your Revit project
* Open a 3D View that displays the geometry you want to export
* Select the Datasmith tab and press "Export 3D View"
* Collect the generated udatasmith file and Assets folder -- you'll move those into your Unity project in the next phase.

### Quick Start (Unity)
* Create a new Unity project using the appropriate Unity version for the VRChat SDK (2018.4.20f1 at the time of this writing)
* Import VRChat's World SDK3
* Checkout this repository to your Assets folder (or otherwise copy these scripts into your project)
* Copy a udatasmith file and its associated Data folder into your Assets
* Drop the prefab generated from the udatasmith file into the Scene.
* Import the VRCWorld prefab from the SDK and move it to set up the spawn point
* Bake lightmaps (or turn off "Setup Lightmap Baking" on the udatasmith asset settings)
* Build and test!

If you get errors about missing assets on the first attempt, right-clicking the udatasmith asset and selecting 'reimport' will usually fix that. The udatasmith importer currently assumes that the udsmesh and texture files have already been processed, which is not guaranteed when adding all of the files at once.

### Supported datatypes
* The world transform hierarchy
* StaticMesh actors and material assignments
* Common Material properties (Diffuse and Bump maps)
* Lights -- early WIP, most Light properties are not yet translated.


