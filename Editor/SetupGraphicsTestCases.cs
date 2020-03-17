using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;
using System.Reflection;
using UnityEditor.XR.Management;
using UnityEngine.XR.Management;

using UnityEditor;
using EditorSceneManagement = UnityEditor.SceneManagement;

namespace UnityEditor.TestTools.Graphics
{
    /// <summary>
    /// Test framework prebuild step to collect reference images for the current test run and prepare them for use in the
    /// player.
    /// Will also build Lightmaps for specially labelled scenes.
    /// </summary>
    public static class SetupGraphicsTestCases
    {
        static readonly string bakeLabel = "TestRunnerBake";

        private static bool IsBuildingForEditorPlaymode
        {
            get
            {
                var playmodeLauncher =
                    typeof(RequirePlatformSupportAttribute).Assembly.GetType(
                        "UnityEditor.TestTools.TestRunner.PlaymodeLauncher");
                var isRunningField = playmodeLauncher.GetField("IsRunning");

                return (bool)isRunningField.GetValue(null);
            }
        }

        public static void Setup(string rootImageTemplatePath = EditorGraphicsTestCaseProvider.ReferenceImagesRoot, string imageResultsPath = "")
        {
            ColorSpace colorSpace;
            BuildTarget buildPlatform;
            RuntimePlatform runtimePlatform;
            GraphicsDeviceType[] graphicsDevices;

            string xrsdk = "None";

            UnityEditor.EditorPrefs.SetBool("AsynchronousShaderCompilation", false);

            // Figure out if we're preparing to run in Editor playmode, or if we're building to run outside the Editor
            if (IsBuildingForEditorPlaymode)
            {
                colorSpace = QualitySettings.activeColorSpace;
                buildPlatform = BuildTarget.NoTarget;
                runtimePlatform = Application.platform;
                graphicsDevices = new[] {SystemInfo.graphicsDeviceType};
            }
            else
            {
                buildPlatform = EditorUserBuildSettings.activeBuildTarget;
                runtimePlatform = Utils.BuildTargetToRuntimePlatform(buildPlatform);
                colorSpace = PlayerSettings.colorSpace;
                graphicsDevices = PlayerSettings.GetGraphicsAPIs(buildPlatform);
            }

            if (PlayerSettings.virtualRealitySupported == true)
            {
                if ((PlayerSettings.GetVirtualRealitySDKs(BuildPipeline.GetBuildTargetGroup(buildPlatform)).Length == 0) && (IsBuildingForEditorPlaymode == true))
                {
                    xrsdk = "None";
                }
                else if ((PlayerSettings.GetVirtualRealitySDKs(BuildPipeline.GetBuildTargetGroup(buildPlatform)).Length == 0) && (IsBuildingForEditorPlaymode == false))
                {
                    xrsdk = "MockHMD";
                }
                else
                {
                    xrsdk = PlayerSettings.GetVirtualRealitySDKs(BuildPipeline.GetBuildTargetGroup(buildPlatform)).First();
                }
            }
            
            var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildPipeline.GetBuildTargetGroup(buildPlatform));

            // Since the settings are null when using NoTarget for the BuildTargetGroup which editor playmode seems to do
            // just use Standalone settings instead.
            if (IsBuildingForEditorPlaymode)
                settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);

            if (settings != null && settings.InitManagerOnStart)
            {
                if (settings.AssignedSettings.loaders.Count > 0)
                {
                    // since we don't really know which runtime loader will actually be used at runtime,
                    // just take the first one assuming it will work and if it isn't loaded the
                    // tests should fail since the reference images bundle will be named
                    // with a loader that isn't active at runtime.
                    var firstLoader = settings.AssignedSettings.loaders.First();

                    if(firstLoader != null)
                    {
                        xrsdk = firstLoader.name;
                    }
                }
            }

            ImageHandler.instance.ImageResultsPath = imageResultsPath;

            var bundleBuilds = new List<AssetBundleBuild>();

            foreach (var api in graphicsDevices)
            {
                var images = EditorGraphicsTestCaseProvider.CollectReferenceImagePathsFor(rootImageTemplatePath, colorSpace, runtimePlatform, api, xrsdk);

                Utils.SetupReferenceImageImportSettings(images.Values);

                if (buildPlatform == BuildTarget.NoTarget)
                    continue;

                bundleBuilds.Add(new AssetBundleBuild
                {
                    assetBundleName = string.Format("referenceimages-{0}-{1}-{2}-{3}", colorSpace, runtimePlatform, api, xrsdk),
                    addressableNames = images.Keys.ToArray(),
                    assetNames = images.Values.ToArray()
                });
            }

            if (bundleBuilds.Count > 0)
            {
                if (!Directory.Exists("Assets/StreamingAssets"))
                    Directory.CreateDirectory("Assets/StreamingAssets");

                foreach (var bundle in bundleBuilds)
                {
                    BuildPipeline.BuildAssetBundles("Assets/StreamingAssets", new [] { bundle }, BuildAssetBundleOptions.None,
                        buildPlatform);
                }
            }

            var buildSettingsScenes = EditorBuildSettings.scenes;
            var sceneIndex = 0;

            var filterGuid = AssetDatabase.FindAssets("t: TestFilters");

            var filters = AssetDatabase.LoadAssetAtPath<TestFilters>(
                AssetDatabase.GUIDToAssetPath(filterGuid.FirstOrDefault()));

            var filterTest = Resources.Load<TestFilters>("TestCaseFilters");

            foreach (var scene in buildSettingsScenes)
            {
                if (!scene.enabled) continue;

                if (filters != null)
                {

                    // Right now only single TestFilter.asset file will be processed
                    var filtersForScene = filters.filters.Where(f => AssetDatabase.GetAssetPath(f.FilteredScene) == scene.path);
                    bool enableScene = true;
                    string filterReasons = "";
                    foreach (var filter in filtersForScene)
                    {
                        StereoRenderingModeFlags stereoModeFlag = 0;

                        switch (PlayerSettings.stereoRenderingPath)
                        {
                            case StereoRenderingPath.MultiPass:
                                stereoModeFlag |= StereoRenderingModeFlags.MultiPass;
                                break;
                            case StereoRenderingPath.SinglePass:
                                stereoModeFlag |= StereoRenderingModeFlags.SinglePass;
                                break;
                            case StereoRenderingPath.Instancing:
                                stereoModeFlag |= StereoRenderingModeFlags.Instancing;
                                break;
                        }

                        if ((filter.BuildPlatform == buildPlatform || filter.BuildPlatform == BuildTarget.NoTarget) &&
                            (filter.GraphicsDevice == graphicsDevices.First() || filter.GraphicsDevice == GraphicsDeviceType.Null) &&
                            (filter.ColorSpace == colorSpace || filter.ColorSpace == ColorSpace.Uninitialized))
                        {
                            // Adding reasons in case when same test is ignored several times
                            filterReasons += filter.Reason + "\n";
                            enableScene = false;

                            // non vr filter matched
                            if ((!PlayerSettings.virtualRealitySupported || !(settings != null && settings.InitManagerOnStart)) &&
                                (string.IsNullOrEmpty(filter.XrSdk) || string.Compare(filter.XrSdk, "None", true) == 0) &&
                                filter.StereoModes == StereoRenderingModeFlags.None)
                            {
                                enableScene = false;
                            }
                            // if VR is enabled then the VR specific filters need to match the filter too
                            else if ((PlayerSettings.virtualRealitySupported || (settings != null && settings.InitManagerOnStart)) &&
                                (filter.StereoModes == StereoRenderingModeFlags.None || (filter.StereoModes & stereoModeFlag) == stereoModeFlag) &&
                                (filter.XrSdk == xrsdk || string.IsNullOrEmpty(filter.XrSdk)))
                            {
                                enableScene = false;
                            }

                            scene.enabled = enableScene;
                            if (!enableScene)
                                Debug.Log(string.Format("Removed scene {0} from build settings because {1}", Path.GetFileNameWithoutExtension(scene.path), filter.Reason));
                        }
                    }
                }

                SceneAsset sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path);

                var labels = new System.Collections.Generic.List<string>(AssetDatabase.GetLabels(sceneAsset));


            // For each scene in the build settings, force build of the lightmaps if it has "DoLightmap" label.
            // Note that in the PreBuildSetup stage, TestRunner has already created a new scene with its testing monobehaviours
            if (scene.enabled == true && labels.Contains(bakeLabel))
                {

                    Scene trScene = EditorSceneManagement.EditorSceneManager.GetSceneAt(0);

                    EditorSceneManagement.EditorSceneManager.OpenScene(scene.path, EditorSceneManagement.OpenSceneMode.Additive);

                    Scene currentScene = EditorSceneManagement.EditorSceneManager.GetSceneAt(1);

                    EditorSceneManagement.EditorSceneManager.SetActiveScene(currentScene);

                    Lightmapping.giWorkflowMode = Lightmapping.GIWorkflowMode.OnDemand;

                    EditorUtility.DisplayProgressBar($"Baking Test Scenes {(sceneIndex + 1).ToString()}/{buildSettingsScenes.Length.ToString()}", $"Baking {sceneAsset.name}", ((float)sceneIndex / buildSettingsScenes.Length));

                    Lightmapping.Bake();

                    // disk cache needs to be cleared to prevent bug 742012 where duplicate lights are double baked
                    Lightmapping.ClearDiskCache();

                    EditorSceneManagement.EditorSceneManager.SaveScene(currentScene);

                    EditorSceneManagement.EditorSceneManager.SetActiveScene(trScene);

                    EditorSceneManagement.EditorSceneManager.CloseScene(currentScene, true);
                }
                sceneIndex++;
            }

            // set the scene list in the build settings window.  Only updating the array will do this.
            EditorBuildSettings.scenes = buildSettingsScenes.Where(s => s.enabled).ToArray();

            EditorUtility.ClearProgressBar();

            if (!IsBuildingForEditorPlaymode)
                new CreateSceneListFileFromBuildSettings().Setup();
        }

        static string lightmapDataGitIgnore = @"Lightmap-*_comp*
LightingData.*
ReflectionProbe-*";

        [MenuItem("Assets/Tests/Toggle Scene for Bake")]
        public static void LabelSceneForBake()
        {
            UnityEngine.Object[] sceneAssets = Selection.GetFiltered(typeof(SceneAsset), SelectionMode.DeepAssets);

            EditorSceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            EditorSceneManagement.SceneSetup[] previousSceneSetup = EditorSceneManagement.EditorSceneManager.GetSceneManagerSetup();

            foreach (UnityEngine.Object sceneAsset in sceneAssets)
            {
                List<string> labels = new System.Collections.Generic.List<string>(AssetDatabase.GetLabels(sceneAsset));

                string scenePath = AssetDatabase.GetAssetPath(sceneAsset);
                string gitIgnorePath = Path.Combine(Path.Combine(Application.dataPath.Substring(0, Application.dataPath.Length - 6), scenePath.Substring(0, scenePath.Length - 6)), ".gitignore");

                if (labels.Contains(bakeLabel))
                {
                    labels.Remove(bakeLabel);
                    File.Delete(gitIgnorePath);
                }
                else
                {
                    labels.Add(bakeLabel);

                    string sceneLightingDataFolder = Path.Combine(Path.GetDirectoryName(scenePath), Path.GetFileNameWithoutExtension(scenePath));
                    if (!AssetDatabase.IsValidFolder(sceneLightingDataFolder))
                        AssetDatabase.CreateFolder(Path.GetDirectoryName(scenePath), Path.GetFileNameWithoutExtension(scenePath));

                    File.WriteAllText(gitIgnorePath, lightmapDataGitIgnore);

                    EditorSceneManagement.EditorSceneManager.OpenScene(scenePath, EditorSceneManagement.OpenSceneMode.Single);
                    EditorSceneManagement.EditorSceneManager.SetActiveScene(EditorSceneManagement.EditorSceneManager.GetSceneAt(0));
                    Lightmapping.giWorkflowMode = Lightmapping.GIWorkflowMode.OnDemand;
                    EditorSceneManagement.EditorSceneManager.SaveScene(EditorSceneManagement.EditorSceneManager.GetSceneAt(0));
                }

                AssetDatabase.SetLabels(sceneAsset, labels.ToArray());
            }
            AssetDatabase.Refresh();

            if (previousSceneSetup.Length == 0)
                EditorSceneManagement.EditorSceneManager.NewScene(EditorSceneManagement.NewSceneSetup.DefaultGameObjects, EditorSceneManagement.NewSceneMode.Single);
            else
                EditorSceneManagement.EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
        }

        [MenuItem("Assets/Tests/Toggle Scene for Bake", true)]
        public static bool LabelSceneForBake_Test()
        {
            return IsSceneAssetSelected();
        }

        public static bool IsSceneAssetSelected()
        {
            UnityEngine.Object[] sceneAssets = Selection.GetFiltered(typeof(SceneAsset), SelectionMode.DeepAssets);

            return sceneAssets.Length != 0;
        }
    }
}
