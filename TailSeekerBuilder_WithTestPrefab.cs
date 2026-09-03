#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;

using UnityEditor;
using UnityEditor.Animations;

using UnityEngine;
using UnityEngine.Animations;

using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;

namespace TailSeek
{
    /// <summary>
    /// Builds a tail-to-tail contact system using VRChat Contacts and the VRLabs Contact Tracker.
    ///
    /// Important implementation details:
    /// - The Contact Tracker and curl receiver are placed on the Hip, which is
    ///   the first bone parented to the Armature.
    ///   The tail sender stays on the Tail Tip so other avatars can detect this tail.
    /// - The VRLabs "Tracker Target" is moved outside the Contact Tracker, as required by
    ///   the VRLabs installation instructions.
    /// - Directional seeking uses Left/Right curl clips on FX, blended by
    ///   ContactTracker/X- and ContactTracker/X+ (same Motion Time as proximity curl).
    /// - The supplied Contact Tracker FX controller is merged into the avatar FX controller.
    /// - The user's curl layer is added after the tracker layers.
    /// </summary>
    public class TailSeekerBuilder : EditorWindow
    {
        private const string GeneratedTrackerName = "TailSeek Contact Tracker";
        private const string GeneratedSenderName = "TailSeek Sender";
        private const string GeneratedCurlReceiverName = "TailSeek Curl Receiver";
        private const string GeneratedTargetName = "TailSeek Tracker Target";
        private const string GeneratedAimProxyName = "TailSeek Aim Proxy";
        private const string GeneratedTestObjectName = "TailSeek Test Object";
        private const string CurlLayerName = "Tail Seek - Curl";
        private const string CurlRemapLayerName = "Tail Seek - Curl Remap";
        private const string DirectionLayerName = "Tail Seek - Direction";
        private const string SeekLayerName = "Tail Seek";

        private const string TrackerControlParameter = "ContactTracker/Control";
        private const string TrackerSizeParameter = "ContactTracker/Size";
        private const string TrackerLeftParameter = "ContactTracker/X-";
        private const string TrackerRightParameter = "ContactTracker/X+";

        private VRCAvatarDescriptor avatar;

        private Transform tailRoot;
        private Transform tailTip;
        private Transform hip;
        private AnimationClip curlAnimation;

        private GameObject contactTrackerPrefab;
        private AnimatorController contactTrackerController;

        private string collisionTag = "TailSeek";

        // Radius of each of the six VRChat proximity receivers.
        private float trackerSize = 2.0f;
        private float senderRadius = 0.025f;

        private bool allowSelf = false;
        private bool allowOthers = true;

        private float curlDistance = 2.0f;
        private float fullCurlDistance = 2.0f;

        private string curlParameter = "TailSeek_Curl";

        private bool addDirectionalCurl = true;
        private AnimationClip leftCurlAnimation;
        private AnimationClip rightCurlAnimation;

        private bool addCurlAnimator = true;
        private bool createTestPrefab = true;
        private float testObjectScale = 0.10f;
        private float testMoveSpeed = 1.5f;
        private string testPrefabPath = "Assets/TailSeek/Generated/TailSeek Test Object.prefab";

        private Vector2 scroll;
        private readonly HashSet<string> expandedHelpIds = new HashSet<string>();
        private GUIContent infoIcon;

        [MenuItem("Tools/Tail Seek/Builder")]
        public static void ShowWindow()
        {
            GetWindow<TailSeekerBuilder>("Tail Seek Builder");
        }

        private void OnEnable()
        {
            TryFindTrackerAssets();
        }

        private void OnGUI()
        {
            TryFindTrackerAssets();
            scroll = EditorGUILayout.BeginScrollView(scroll);

            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 220f;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("TAIL SEEK BUILDER", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Builds tail-to-hip interaction using VRChat Contacts and the VRLabs Contact Tracker.\n\n" +
                "The tracker and curl receiver are placed on the Hip. " +
                "The tail sender stays on the Tail Tip so other avatars can detect this tail. " +
                "Left/Right curl clips on FX turn the tail from ContactTracker/X- and X+.",
                MessageType.Info);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Avatar", EditorStyles.boldLabel);

            avatar = HelpObjectField(
                "avatar",
                "Avatar",
                avatar,
                "The VRCAvatarDescriptor to build on.",
                true);

            hip = avatar != null ? FindHipsBone() : null;

            if (avatar != null)
            {
                if (hip != null)
                {
                    HelpValueLabel(
                        "hip",
                        "Hip (auto)",
                        hip.name,
                        "The first bone parented to the Armature. The Contact Tracker and wrap receiver are placed here.");
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Could not find the Hip. The first bone parented to the Armature is used automatically.",
                        MessageType.Warning);
                }
            }

            if (contactTrackerPrefab != null &&
                contactTrackerController != null)
            {
                HelpValueLabel(
                    "tracker",
                    "VRLabs Tracker (auto)",
                    contactTrackerPrefab.name + " + FX",
                    "The VRLabs Contact Tracker prefab and FX controller. Found automatically and always merged into the avatar FX controller.");
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Could not find the VRLabs Contact Tracker prefab and FX controller. Import Contact Tracker into this project.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Tail", EditorStyles.boldLabel);

            tailRoot = HelpObjectField(
                "tailRoot",
                "Tail Root",
                tailRoot,
                "First bone of the tail chain.",
                true);

            tailTip = HelpObjectField(
                "tailTip",
                "Tail Tip",
                tailTip,
                "End of the tail. The TailSeek sender is placed here so other avatars can detect this tail.",
                true);

            curlAnimation = HelpObjectField(
                "wrapAnim",
                "Tail Wrapping Animation",
                curlAnimation,
                "Existing tail wrapping animation. Frame 0 = rest, last frame = full wrap.",
                false);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(
                "Directional Seeking",
                EditorStyles.boldLabel);

            addDirectionalCurl = HelpToggle(
                "leftRight",
                "Create Left / Right Curl",
                addDirectionalCurl,
                "Plays Left and Right curl clips from ContactTracker/X- and ContactTracker/X+.\n\n" +
                "Unity constraints cannot rotate humanoid / FX bones reliably in VRChat — the animator overwrites them. " +
                "These clips run on FX instead, the same way proximity wrapping already works.\n\n" +
                "Key the tail bone rotation you want (Z or otherwise) plus any extra shapes. " +
                "The direction layer stays Idle while you are centered so the main wrap can play. " +
                "ContactTracker/X- plays Left. ContactTracker/X+ plays Right. " +
                "Proximity still scrubs the clip through TailSeek_Curl_Time.");

            if (addDirectionalCurl)
            {
                leftCurlAnimation = HelpObjectField(
                    "leftAnim",
                    "Left Curl Animation",
                    leftCurlAnimation,
                    "Timed clip for the other user on this avatar's left. Frame 0 = rest, last frame = full left curl.",
                    false);

                rightCurlAnimation = HelpObjectField(
                    "rightAnim",
                    "Right Curl Animation",
                    rightCurlAnimation,
                    "Timed clip for the other user on this avatar's right. Frame 0 = rest, last frame = full right curl.",
                    false);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Contact Settings", EditorStyles.boldLabel);

            collisionTag = HelpTextField(
                "tag",
                "Collision Tag",
                collisionTag,
                "Shared tag for senders and receivers. Must match on interacting avatars.");

            trackerSize = HelpFloatField(
                "trackerRadius",
                "Tracker Receiver Radius",
                trackerSize,
                "Radius of each of the six directional tracker receivers (X+, X-, Y+, Y-, Z+, Z-). Keep this at least as large as Curl Start Distance.");

            senderRadius = HelpFloatField(
                "senderRadius",
                "Tail Sender Radius",
                senderRadius,
                "Radius of the Contact Sender at the tail tip.");

            allowSelf = HelpToggle(
                "allowSelf",
                "Allow Self",
                allowSelf,
                "Leave off so your own tail sender cannot wrap your own tail.");

            allowOthers = HelpToggle(
                "allowOthers",
                "Allow Others",
                allowOthers,
                "Other avatars' senders can trigger your hip receivers.");

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Curl", EditorStyles.boldLabel);

            addCurlAnimator = HelpToggle(
                "curlAnimator",
                "Create Curl Animator",
                addCurlAnimator,
                "Adds the wrap float and the Motion Time FX layer.\n\n" +
                "The wrap clip is driven like VRCFury Depth Animation (Motion Time). " +
                "Outside Curl Start Distance the clip is at frame 0. At Full Curl Distance " +
                "and closer, the clip is on its last frame. Defaults: start 2, full 2 — " +
                "so anything inside a radius of 2 is full wrap. Raise Start above Full " +
                "if you want the blendshape sets to ramp in before that.");

            if (addCurlAnimator)
            {
                curlDistance = HelpFloatField(
                    "curlStart",
                    "Curl Start Distance",
                    curlDistance,
                    "Radius of the hip proximity receiver. Outside this radius the clip is at frame 0.");

                fullCurlDistance = HelpFloatField(
                    "curlFull",
                    "Full Curl Distance",
                    fullCurlDistance,
                    "Radius at which the clip reaches its last frame. Use 2 for full wrap anywhere inside a 2-unit sphere. Must be greater than 0 and no larger than Curl Start Distance.");

                curlParameter = HelpTextField(
                    "curlParam",
                    "Curl Parameter",
                    curlParameter,
                    "FX float driven by the hip proximity receiver.");
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Play Mode Test Object", EditorStyles.boldLabel);

            createTestPrefab = HelpToggle(
                "testPrefab",
                "Create Test Prefab",
                createTestPrefab,
                "Creates a visible Contact Sender prefab for testing, then places it on the assigned avatar at the hip.\n\n" +
                "Saved at:\n" + testPrefabPath +
                "\n\nEnter Play Mode, then select Gesture Manager. " +
                "Move with W/A/S/D and Q/E (Shift to go faster), or drag it in the Scene view.\n\n" +
                "Delete the test object before uploading the avatar.");

            if (createTestPrefab)
            {
                testObjectScale = HelpFloatField(
                    "testScale",
                    "Test Object Scale",
                    testObjectScale,
                    "Visual diameter/scale of the generated test sphere.");

                testMoveSpeed = HelpFloatField(
                    "testSpeed",
                    "Test Move Speed",
                    testMoveSpeed,
                    "Keyboard movement speed in Play Mode.");
            }

            EditorGUILayout.Space(15);
            EditorGUILayout.LabelField("Existing System", EditorStyles.boldLabel);

            bool valid =
                avatar != null &&
                tailRoot != null &&
                tailTip != null &&
                hip != null &&
                contactTrackerPrefab != null &&
                contactTrackerController != null &&
                (!addCurlAnimator || curlAnimation != null) &&
                (!addDirectionalCurl ||
                 (leftCurlAnimation != null && rightCurlAnimation != null));

            if (!valid)
            {
                EditorGUILayout.HelpBox(
                    "Assign Avatar, Tail Root, Tail Tip, Tail Wrapping Animation when Create Curl Animator is enabled, " +
                    "and Left/Right Curl when Create Left / Right Curl is enabled. " +
                    "The VRLabs Contact Tracker is found automatically.",
                    MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();

            GUI.enabled = valid;
            if (GUILayout.Button(
                "REPLACE / REBUILD EXISTING TAIL SEEK SYSTEM",
                GUILayout.Height(36)))
            {
                if (EditorUtility.DisplayDialog(
                    "Replace Existing Tail Seek System",
                    "This will delete previous Tail Seek objects, contacts, FX layers, and parameters " +
                    "on '" + avatar.gameObject.name + "', then rebuild with the current builder settings.\n\n" +
                    "Continue?",
                    "Replace And Rebuild",
                    "Cancel"))
                {
                    Build();
                }
            }

            GUI.enabled = avatar != null;
            if (GUILayout.Button(
                "REMOVE EXISTING TAIL SEEK SYSTEM",
                GUILayout.Height(36)))
            {
                if (EditorUtility.DisplayDialog(
                    "Remove Existing Tail Seek System",
                    "This will delete previous Tail Seek objects, contacts, FX layers, and parameters " +
                    "on '" + avatar.gameObject.name + "' without rebuilding.\n\n" +
                    "Continue?",
                    "Remove",
                    "Cancel"))
                {
                    RemoveExistingSystemStandalone();
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            GUI.enabled = valid;
            if (GUILayout.Button(
                "BUILD TAIL SEEK SYSTEM",
                GUILayout.Height(36)))
            {
                Build();
            }

            GUI.enabled = createTestPrefab && avatar != null;
            if (GUILayout.Button(
                "CREATE / RECREATE TEST PREFAB",
                GUILayout.Height(36)))
            {
                try
                {
                    CreateTestObjectPrefab();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    EditorUtility.DisplayDialog("Test Prefab Error", exception.Message, "OK");
                }
            }

            EditorGUILayout.EndHorizontal();
            GUI.enabled = true;

            EditorGUIUtility.labelWidth = previousLabelWidth;
            EditorGUILayout.EndScrollView();
        }

        // =====================================================================
        // CLICK-TO-EXPAND FIELD HELP
        // =====================================================================

        private GUIContent InfoIcon
        {
            get
            {
                if (infoIcon == null)
                {
                    string iconName = EditorGUIUtility.isProSkin
                        ? "d_console.infoicon.sml"
                        : "console.infoicon.sml";

                    infoIcon = EditorGUIUtility.IconContent(iconName);
                    if (infoIcon == null || infoIcon.image == null)
                        infoIcon = EditorGUIUtility.IconContent("console.infoicon.sml");
                }

                return infoIcon;
            }
        }

        private T HelpObjectField<T>(
            string id,
            string label,
            T value,
            string help,
            bool allowSceneObjects)
            where T : UnityEngine.Object
        {
            Rect fieldRect = DrawHelpPrefix(id, label);
            value = (T)EditorGUI.ObjectField(
                fieldRect,
                value,
                typeof(T),
                allowSceneObjects);
            DrawExpandedHelp(id, help);
            return value;
        }

        private bool HelpToggle(
            string id,
            string label,
            bool value,
            string help)
        {
            Rect fieldRect = DrawHelpPrefix(id, label);
            value = EditorGUI.Toggle(fieldRect, value);
            DrawExpandedHelp(id, help);
            return value;
        }

        private float HelpFloatField(
            string id,
            string label,
            float value,
            string help)
        {
            Rect fieldRect = DrawHelpPrefix(id, label);
            value = EditorGUI.FloatField(fieldRect, value);
            DrawExpandedHelp(id, help);
            return value;
        }

        private string HelpTextField(
            string id,
            string label,
            string value,
            string help)
        {
            Rect fieldRect = DrawHelpPrefix(id, label);
            value = EditorGUI.TextField(fieldRect, value);
            DrawExpandedHelp(id, help);
            return value;
        }

        private void HelpValueLabel(
            string id,
            string label,
            string value,
            string help)
        {
            Rect fieldRect = DrawHelpPrefix(id, label);
            EditorGUI.LabelField(fieldRect, value);
            DrawExpandedHelp(id, help);
        }

        private Rect DrawHelpPrefix(string id, string label)
        {
            Rect row = EditorGUILayout.GetControlRect();
            float indent = EditorGUI.indentLevel * 15f;
            float iconSize = 16f;
            float gap = 3f;

            Rect iconRect = new Rect(
                row.x + indent,
                row.y + Mathf.Max(0f, (row.height - iconSize) * 0.5f),
                iconSize,
                iconSize);

            Rect nameRect = new Rect(
                iconRect.xMax + gap,
                row.y,
                EditorGUIUtility.labelWidth - indent - iconSize - gap,
                row.height);

            Rect fieldRect = new Rect(
                row.x + EditorGUIUtility.labelWidth,
                row.y,
                Mathf.Max(0f, row.width - EditorGUIUtility.labelWidth),
                row.height);

            if (GUI.Button(iconRect, InfoIcon, EditorStyles.label) ||
                GUI.Button(nameRect, label, EditorStyles.label))
            {
                ToggleHelp(id);
            }

            EditorGUIUtility.AddCursorRect(iconRect, MouseCursor.Link);
            EditorGUIUtility.AddCursorRect(nameRect, MouseCursor.Link);
            return fieldRect;
        }

        private void DrawExpandedHelp(string id, string help)
        {
            if (string.IsNullOrEmpty(help) ||
                !expandedHelpIds.Contains(id))
            {
                return;
            }

            EditorGUILayout.HelpBox(help, MessageType.Info);
        }

        private void ToggleHelp(string id)
        {
            if (!expandedHelpIds.Add(id))
                expandedHelpIds.Remove(id);
        }

        // =====================================================================
        // FIND VRLABS ASSETS
        // =====================================================================

        private void TryFindTrackerAssets()
        {
            if (contactTrackerPrefab == null)
            {
                contactTrackerPrefab = FindNamedAsset<GameObject>(
                    "Contact Tracker",
                    "t:Prefab");
            }

            if (contactTrackerController == null)
            {
                contactTrackerController = FindNamedAsset<AnimatorController>(
                    "Contact Tracker FX",
                    "t:AnimatorController");
            }
        }

        private static T FindNamedAsset<T>(string assetName, string filter)
            where T : UnityEngine.Object
        {
            string[] guids = AssetDatabase.FindAssets(assetName + " " + filter);

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                T asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null && asset.name == assetName)
                    return asset;
            }

            return null;
        }

        private Transform FindHipsBone()
        {
            Transform armature = FindArmature();
            if (armature == null)
                return null;

            for (int i = 0; i < armature.childCount; i++)
            {
                Transform child = armature.GetChild(i);
                if (child == null)
                    continue;

                if (IsGeneratedObjectName(child.name))
                    continue;

                return child;
            }

            return null;
        }

        private Transform FindArmature()
        {
            if (avatar == null)
                return null;

            Transform namedArmature =
                FindChildRecursiveIgnoreCase(
                    avatar.transform,
                    "Armature");

            if (namedArmature != null)
                return namedArmature;

            Animator animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
            {
                Transform humanHips =
                    animator.GetBoneTransform(HumanBodyBones.Hips);

                if (humanHips != null &&
                    humanHips.parent != null)
                {
                    return humanHips.parent;
                }
            }

            return null;
        }

        private static bool IsGeneratedObjectName(string objectName)
        {
            return objectName.StartsWith("TailSeek", StringComparison.OrdinalIgnoreCase) ||
                   objectName.StartsWith("Tail Seek", StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================
        // BUILD
        // =====================================================================

        private void Build()
        {
            int undoGroup =
                Undo.GetCurrentGroup();

            Undo.SetCurrentGroupName(
                "Build Tail Seek System");

            try
            {
                Validate();

                GameObject avatarObject =
                    avatar.gameObject;

                RemoveExistingGeneratedObjects(
                    avatarObject.transform);

                // -------------------------------------------------------------
                // CREATE CONTACT TRACKER
                // -------------------------------------------------------------

                GameObject trackerObject =
                    PrefabUtility.InstantiatePrefab(
                        contactTrackerPrefab,
                        avatarObject.transform) as GameObject;

                if (trackerObject == null)
                {
                    throw new Exception(
                        "Could not instantiate the Contact Tracker prefab.");
                }

                Undo.RegisterCreatedObjectUndo(
                    trackerObject,
                    "Create Contact Tracker");

                trackerObject.name =
                    GeneratedTrackerName;

                // VRLabs instructs users to unpack the prefab before modifying
                // its hierarchy.
                if (PrefabUtility.IsPartOfPrefabInstance(
                    trackerObject))
                {
                    PrefabUtility.UnpackPrefabInstance(
                        trackerObject,
                        PrefabUnpackMode.Completely,
                        InteractionMode.AutomatedAction);
                }

                Transform trackerRoot =
                    trackerObject.transform;

                RemoveTrackerDemoContainer(
                    trackerRoot);

                // Initialize the tracker in WORLD SPACE at the hip.
                // It is parented to the avatar root, not the hip bone, so it
                // is not pulled by tail animation. It still sits at the hip
                // so receivers detect other tails near the pelvis.
                trackerRoot.position =
                    hip.position;

                trackerRoot.rotation =
                    hip.rotation;

                trackerRoot.localScale =
                    Vector3.one;

                ConfigureTrackerReceivers(
                    trackerRoot);

                // -------------------------------------------------------------
                // FIND TRACKER TARGET
                // -------------------------------------------------------------

                Transform trackerTarget =
                    FindChildRecursive(
                        trackerRoot,
                        "Tracker Target");

                if (trackerTarget == null)
                {
                    trackerTarget =
                        FindChildRecursive(
                            trackerRoot,
                            "TrackerTarget");
                }

                if (trackerTarget == null)
                {
                    throw new Exception(
                        "The Contact Tracker prefab does not contain a 'Tracker Target'.\n\n" +
                        "Please use the official VRLabs Contact Tracker prefab.");
                }

                // VRLabs' installation requires Tracker Target to be moved
                // outside Contact Tracker.
                //
                // Preserve its world transform while reparenting.
                Vector3 targetWorldPosition =
                    trackerTarget.position;

                Quaternion targetWorldRotation =
                    trackerTarget.rotation;

                trackerTarget.SetParent(
                    avatarObject.transform,
                    true);

                trackerTarget.name =
                    GeneratedTargetName;

                trackerTarget.position =
                    targetWorldPosition;

                trackerTarget.rotation =
                    targetWorldRotation;

                trackerTarget.localScale =
                    Vector3.one;

                // -------------------------------------------------------------
                // TAIL SENDER
                // -------------------------------------------------------------

                Transform senderTransform =
                    FindOrCreateChild(
                        tailTip,
                        GeneratedSenderName);

                senderTransform.localPosition =
                    Vector3.zero;

                senderTransform.localRotation =
                    Quaternion.identity;

                senderTransform.localScale =
                    Vector3.one;

                VRCContactSender sender =
                    senderTransform.GetComponent<
                        VRCContactSender>();

                if (sender == null)
                {
                    sender =
                        Undo.AddComponent<
                            VRCContactSender>(
                            senderTransform.gameObject);
                }

                ConfigureSender(sender);

                // -------------------------------------------------------------
                // CURL RECEIVER
                // -------------------------------------------------------------

                Transform curlTransform =
                    FindOrCreateChild(
                        hip,
                        GeneratedCurlReceiverName);

                curlTransform.localPosition =
                    Vector3.zero;

                curlTransform.localRotation =
                    Quaternion.identity;

                curlTransform.localScale =
                    Vector3.one;

                VRCContactReceiver curlReceiver =
                    curlTransform.GetComponent<
                        VRCContactReceiver>();

                if (curlReceiver == null)
                {
                    curlReceiver =
                        Undo.AddComponent<
                            VRCContactReceiver>(
                            curlTransform.gameObject);
                }

                ConfigureCurlReceiver(
                    curlReceiver);

                // -------------------------------------------------------------
                // FX CONTROLLER
                // -------------------------------------------------------------

                AnimatorController fxController =
                    GetFXController();

                if (fxController == null)
                {
                    throw new Exception(
                        "The avatar's FX Playable Layer does not have an Animator Controller assigned.");
                }

                MergeTrackerFXController(
                    fxController,
                    contactTrackerController);

                if (addCurlAnimator || addDirectionalCurl)
                {
                    AddCurlParameter(
                        fxController);

                    AddSeekLayer(
                        fxController);
                }

                // -------------------------------------------------------------
                // SAVE
                // -------------------------------------------------------------

                EditorUtility.SetDirty(
                    avatar);

                EditorUtility.SetDirty(
                    fxController);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                if (createTestPrefab)
                {
                    GameObject testObject = CreateTestObjectPrefab();
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    if (testObject != null)
                        Selection.activeGameObject = testObject;
                }
                else
                {
                    Selection.activeGameObject =
                        trackerObject;
                }

                EditorUtility.DisplayDialog(
                    "Tail Seek Built",
                    "Tail Seek was successfully generated.\n\n" +
                    "Tracker and curl receiver initialized at Hip:\n" +
                    hip.name +
                    "\n\nTail sender remains on Tail Tip:\n" +
                    tailTip.name +
                    "\n\nCollision Tag:\n" +
                    collisionTag +
                    "\n\nVRLabs Contact Tracker FX was merged into the avatar FX controller." +
                    (addDirectionalCurl
                        ? "\n\nLeft/Right curl is driven by ContactTracker/X- and X+."
                        : ""),
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(
                    exception);

                EditorUtility.DisplayDialog(
                    "Tail Seek Build Error",
                    exception.Message,
                    "OK");
            }
            finally
            {
                Undo.CollapseUndoOperations(
                    undoGroup);
            }
        }

        // =====================================================================
        // PLAY MODE TEST PREFAB
        // =====================================================================

        private GameObject CreateTestObjectPrefab()
        {
            if (string.IsNullOrWhiteSpace(collisionTag))
                throw new Exception("Collision Tag cannot be empty before creating the test prefab.");

            if (testObjectScale <= 0)
                throw new Exception("Test Object Scale must be greater than zero.");

            if (testMoveSpeed <= 0)
                throw new Exception("Test Move Speed must be greater than zero.");

            EnsureFolder("Assets", "TailSeek", "Generated");
            RemoveSceneTestObjects();

            GameObject testObject = new GameObject(GeneratedTestObjectName);
            Undo.RegisterCreatedObjectUndo(
                testObject,
                "Create Tail Seek Test Object");

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "Visual";
            visual.transform.SetParent(testObject.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one * testObjectScale;

            Collider collider = visual.GetComponent<Collider>();
            if (collider != null)
                DestroyImmediate(collider);

            VRCContactSender sender = testObject.AddComponent<VRCContactSender>();
            ConfigureSender(sender);
            sender.radius = Mathf.Max(senderRadius, testObjectScale * 0.5f);
            sender.ApplyConfigurationChanges();

            TailSeekTestObject mover = testObject.AddComponent<TailSeekTestObject>();
            mover.moveSpeed = testMoveSpeed;
            mover.collisionTag = collisionTag;
            mover.fullCurlDistance = fullCurlDistance;
            mover.simulateContacts = true;

            string prefabPath = testPrefabPath.Replace("\\", "/");
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null)
                AssetDatabase.DeleteAsset(prefabPath);

            GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(
                testObject,
                prefabPath,
                InteractionMode.AutomatedAction);

            if (prefab == null)
            {
                DestroyImmediate(testObject);
                throw new Exception("Unity could not save the TailSeek Test Object prefab at " + prefabPath);
            }

            PlaceTestObjectOnAvatar(testObject);
            Debug.Log("Tail Seek: created test prefab at " + prefabPath);
            return testObject;
        }

        private void RemoveSceneTestObjects()
        {
            TailSeekTestObject[] existing =
                FindObjectsOfType<TailSeekTestObject>();

            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] == null)
                    continue;

                Undo.DestroyObjectImmediate(existing[i].gameObject);
            }
        }

        private void PlaceTestObjectOnAvatar(GameObject testObject)
        {
            if (testObject == null)
                return;

            if (avatar == null)
            {
                Debug.LogWarning(
                    "Tail Seek: assign an Avatar to place the test object on the avatar automatically.");
                return;
            }

            if (hip == null)
                hip = FindHipsBone();

            Transform placeAt =
                hip != null ? hip : avatar.transform;

            testObject.transform.SetParent(avatar.transform, true);
            testObject.transform.position = placeAt.position;
            testObject.transform.rotation = Quaternion.identity;
            testObject.transform.localScale = Vector3.one;
            testObject.name = GeneratedTestObjectName;
            Selection.activeGameObject = testObject;
        }

        private static void EnsureFolder(params string[] parts)
        {
            if (parts == null || parts.Length == 0)
                return;

            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        // =====================================================================
        // VALIDATION
        // =====================================================================

        private void Validate()
        {
            TryFindTrackerAssets();

            if (avatar == null)
                throw new Exception(
                    "Avatar is not assigned.");

            if (tailRoot == null)
                throw new Exception(
                    "Tail Root is not assigned.");

            if (tailTip == null)
                throw new Exception(
                    "Tail Tip is not assigned.");

            hip = FindHipsBone();

            if (hip == null)
            {
                throw new Exception(
                    "Could not find the Hip bone. " +
                    "The first bone parented to the Armature is used automatically.");
            }

            if (contactTrackerPrefab == null)
                throw new Exception(
                    "Could not find the VRLabs Contact Tracker prefab. Import Contact Tracker into this project.");

            if (contactTrackerController == null)
                throw new Exception(
                    "Could not find the VRLabs Contact Tracker FX controller. Import Contact Tracker into this project.");

            if (addCurlAnimator &&
                curlAnimation == null)
            {
                throw new Exception(
                    "Tail Wrapping Animation is not assigned.");
            }

            if (string.IsNullOrWhiteSpace(
                collisionTag))
            {
                throw new Exception(
                    "Collision Tag cannot be empty.");
            }

            if (trackerSize <= 0)
            {
                throw new Exception(
                    "Tracker Receiver Radius must be greater than zero.");
            }

            if (senderRadius <= 0)
            {
                throw new Exception(
                    "Sender Radius must be greater than zero.");
            }

            if (addCurlAnimator)
            {
                if (curlDistance <= 0)
                {
                    throw new Exception(
                        "Curl Start Distance must be greater than zero.");
                }

                if (fullCurlDistance <= 0 ||
                    fullCurlDistance > curlDistance)
                {
                    throw new Exception(
                        "Full Curl Distance must be greater than 0 " +
                        "and no larger than Curl Start Distance.");
                }

                if (string.IsNullOrWhiteSpace(
                    curlParameter))
                {
                    throw new Exception(
                        "Curl Parameter cannot be empty.");
                }
            }

            if (addDirectionalCurl)
            {
                if (leftCurlAnimation == null)
                {
                    throw new Exception(
                        "Left Curl Animation is not assigned.");
                }

                if (rightCurlAnimation == null)
                {
                    throw new Exception(
                        "Right Curl Animation is not assigned.");
                }
            }

            if (!IsDescendantOf(
                tailTip,
                avatar.transform))
            {
                throw new Exception(
                    "Tail Tip must be inside the selected Avatar hierarchy.");
            }

            if (!IsDescendantOf(
                tailRoot,
                avatar.transform))
            {
                throw new Exception(
                    "Tail Root must be inside the selected Avatar hierarchy.");
            }

            if (!IsDescendantOf(
                hip,
                avatar.transform) &&
                hip != avatar.transform)
            {
                throw new Exception(
                    "Hip must be inside the selected Avatar hierarchy.");
            }

            if (!IsDescendantOrSame(
                tailTip,
                tailRoot))
            {
                throw new Exception(
                    "Tail Tip must be the same transform as Tail Root " +
                    "or a descendant of Tail Root.");
            }
        }

        private bool IsDescendantOf(
            Transform child,
            Transform possibleParent)
        {
            if (child == null ||
                possibleParent == null)
            {
                return false;
            }

            Transform current =
                child.parent;

            while (current != null)
            {
                if (current == possibleParent)
                    return true;

                current = current.parent;
            }

            return false;
        }

        private bool IsDescendantOrSame(
            Transform child,
            Transform possibleParent)
        {
            return child == possibleParent ||
                   IsDescendantOf(
                       child,
                       possibleParent);
        }

        // =====================================================================
        // REMOVE PREVIOUS GENERATED SYSTEM
        // =====================================================================

        private void RemoveExistingSystemStandalone()
        {
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove Tail Seek System");

            try
            {
                if (avatar == null)
                    throw new Exception("Avatar is not assigned.");
                RemoveExistingGeneratedObjects(avatar.transform);

                EditorUtility.SetDirty(avatar);

                AnimatorController fxController = GetFXController();
                if (fxController != null)
                    EditorUtility.SetDirty(fxController);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                EditorUtility.DisplayDialog(
                    "Tail Seek Removed",
                    "Previous Tail Seek objects, contacts, layers, and parameters were removed from " +
                    avatar.gameObject.name + ".",
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog(
                    "Tail Seek Remove Error",
                    exception.Message,
                    "OK");
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        private void RemoveExistingGeneratedObjects(
            Transform avatarRoot)
        {
            if (avatarRoot == null)
                return;

            RemoveGeneratedHierarchy(avatarRoot);
            RemoveGeneratedContacts(avatarRoot);
            RemoveGeneratedAimConstraints(avatarRoot);
            RemoveGeneratedRotationConstraints(avatarRoot);

            AnimatorController fxController = GetFXController();
            if (fxController != null)
            {
                RemoveGeneratedFxLayers(fxController);
                RemoveGeneratedFxParameters(fxController);
                EditorUtility.SetDirty(fxController);
            }

            RemoveGeneratedExpressionParameters();
        }

        private void RemoveGeneratedHierarchy(Transform avatarRoot)
        {
            List<GameObject> toDestroy = new List<GameObject>();
            CollectGeneratedObjects(avatarRoot, toDestroy);

            toDestroy.Sort(
                (a, b) => GetDepth(b.transform).CompareTo(GetDepth(a.transform)));

            foreach (GameObject generated in toDestroy)
            {
                if (generated != null)
                    Undo.DestroyObjectImmediate(generated);
            }
        }

        private void CollectGeneratedObjects(
            Transform parent,
            List<GameObject> toDestroy)
        {
            if (parent == null)
                return;

            foreach (Transform child in parent)
            {
                CollectGeneratedObjects(child, toDestroy);

                if (IsGeneratedObject(child) &&
                    !toDestroy.Contains(child.gameObject))
                {
                    toDestroy.Add(child.gameObject);
                }
            }
        }

        private bool IsGeneratedObject(Transform transform)
        {
            if (transform == null)
                return false;

            if (avatar != null &&
                transform == avatar.transform)
            {
                return false;
            }

            if (tailRoot != null && transform == tailRoot)
                return false;

            if (tailTip != null && transform == tailTip)
                return false;

            if (hip != null && transform == hip)
                return false;

            string objectName = transform.name;

            if (objectName == GeneratedTrackerName ||
                objectName == GeneratedSenderName ||
                objectName == GeneratedCurlReceiverName ||
                objectName == GeneratedTargetName ||
                objectName == GeneratedAimProxyName ||
                objectName == "Tracker Target" ||
                objectName == "TrackerTarget")
            {
                return true;
            }

            if (objectName.StartsWith("TailSeek", StringComparison.OrdinalIgnoreCase) ||
                objectName.StartsWith("Tail Seek", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (objectName == "Contact Tracker")
                return HasTailSeekContact(transform);

            return false;
        }

        private void RemoveGeneratedContacts(Transform avatarRoot)
        {
            VRCContactSender[] senders =
                avatarRoot.GetComponentsInChildren<VRCContactSender>(true);

            foreach (VRCContactSender sender in senders)
            {
                if (sender == null)
                    continue;

                if (!IsGeneratedContactObject(sender.gameObject, sender.collisionTags))
                    continue;

                if (IsGeneratedObject(sender.transform) ||
                    sender.gameObject.name == GeneratedSenderName)
                {
                    continue;
                }

                Undo.DestroyObjectImmediate(sender);
            }

            VRCContactReceiver[] receivers =
                avatarRoot.GetComponentsInChildren<VRCContactReceiver>(true);

            foreach (VRCContactReceiver receiver in receivers)
            {
                if (receiver == null)
                    continue;

                if (!IsGeneratedContactObject(receiver.gameObject, receiver.collisionTags) &&
                    !IsGeneratedReceiverParameter(receiver.parameter))
                {
                    continue;
                }

                if (IsGeneratedObject(receiver.transform))
                    continue;

                Undo.DestroyObjectImmediate(receiver);
            }
        }

        private bool IsGeneratedContactObject(
            GameObject obj,
            IEnumerable<string> collisionTags)
        {
            if (obj == null)
                return false;

            if (IsGeneratedObject(obj.transform))
                return true;

            return HasCollisionTag(collisionTags, collisionTag) ||
                   HasCollisionTag(collisionTags, "TailSeek");
        }

        private bool IsGeneratedReceiverParameter(string parameter)
        {
            if (string.IsNullOrEmpty(parameter))
                return false;

            if (!string.IsNullOrWhiteSpace(curlParameter) &&
                parameter == curlParameter)
            {
                return true;
            }

            return parameter.StartsWith("TailSeek", StringComparison.OrdinalIgnoreCase) ||
                   parameter.StartsWith("ContactTracker/", StringComparison.Ordinal);
        }

        private bool HasTailSeekContact(Transform transform)
        {
            VRCContactReceiver[] receivers =
                transform.GetComponentsInChildren<VRCContactReceiver>(true);

            foreach (VRCContactReceiver receiver in receivers)
            {
                if (receiver != null &&
                    (HasCollisionTag(receiver.collisionTags, collisionTag) ||
                     HasCollisionTag(receiver.collisionTags, "TailSeek") ||
                     IsGeneratedReceiverParameter(receiver.parameter)))
                {
                    return true;
                }
            }

            VRCContactSender[] senders =
                transform.GetComponentsInChildren<VRCContactSender>(true);

            foreach (VRCContactSender sender in senders)
            {
                if (sender != null &&
                    (HasCollisionTag(sender.collisionTags, collisionTag) ||
                     HasCollisionTag(sender.collisionTags, "TailSeek")))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasCollisionTag(
            IEnumerable<string> tags,
            string tag)
        {
            if (tags == null ||
                string.IsNullOrEmpty(tag))
            {
                return false;
            }

            foreach (string existing in tags)
            {
                if (existing == tag)
                    return true;
            }

            return false;
        }

        private void RemoveGeneratedAimConstraints(Transform avatarRoot)
        {
            List<Transform> constraintRoots = new List<Transform>();

            if (tailRoot != null)
                constraintRoots.Add(tailRoot);

            if (avatarRoot != null &&
                !constraintRoots.Contains(avatarRoot))
            {
                constraintRoots.Add(avatarRoot);
            }

            foreach (Transform root in constraintRoots)
            {
                AimConstraint[] constraints =
                    root.GetComponentsInChildren<AimConstraint>(true);

                foreach (AimConstraint constraint in constraints)
                {
                    if (constraint == null)
                        continue;

                    if (!IsGeneratedAimConstraint(constraint))
                        continue;

                    Undo.DestroyObjectImmediate(constraint);
                }
            }
        }

        private bool IsGeneratedAimConstraint(AimConstraint constraint)
        {
            for (int i = 0; i < constraint.sourceCount; i++)
            {
                ConstraintSource source = constraint.GetSource(i);
                if (source.sourceTransform == null)
                    continue;

                string sourceName = source.sourceTransform.name;
                if (sourceName == GeneratedTargetName ||
                    sourceName == "Tracker Target" ||
                    sourceName == "TrackerTarget" ||
                    sourceName.StartsWith("TailSeek", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void RemoveGeneratedRotationConstraints(Transform avatarRoot)
        {
            if (avatarRoot == null)
                return;

            RotationConstraint[] constraints =
                avatarRoot.GetComponentsInChildren<RotationConstraint>(true);

            foreach (RotationConstraint constraint in constraints)
            {
                if (constraint == null)
                    continue;

                bool generated = false;
                for (int i = 0; i < constraint.sourceCount; i++)
                {
                    ConstraintSource source = constraint.GetSource(i);
                    if (source.sourceTransform == null)
                        continue;

                    string sourceName = source.sourceTransform.name;
                    if (sourceName == GeneratedAimProxyName ||
                        sourceName == GeneratedTargetName ||
                        sourceName.StartsWith("TailSeek", StringComparison.OrdinalIgnoreCase))
                    {
                        generated = true;
                        break;
                    }
                }

                if (generated)
                    Undo.DestroyObjectImmediate(constraint);
            }
        }

        private void RemoveGeneratedFxLayers(AnimatorController controller)
        {
            for (int i = controller.layers.Length - 1; i >= 0; i--)
            {
                if (IsGeneratedFxLayer(controller.layers[i].name))
                    controller.RemoveLayer(i);
            }
        }

        private bool IsGeneratedFxLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return false;

            if (layerName == CurlLayerName ||
                layerName == SeekLayerName ||
                layerName == "Contact Tracker Control" ||
                layerName == "Contact Tracker Blend Tree")
            {
                return true;
            }

            if (layerName.StartsWith("Tail Seek", StringComparison.OrdinalIgnoreCase))
                return true;

            if (layerName.StartsWith("Contact Tracker Control ", StringComparison.Ordinal) ||
                layerName.StartsWith("Contact Tracker Blend Tree ", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private void RemoveGeneratedFxParameters(AnimatorController controller)
        {
            for (int i = controller.parameters.Length - 1; i >= 0; i--)
            {
                string parameterName = controller.parameters[i].name;
                if (IsGeneratedFxParameter(parameterName))
                    controller.RemoveParameter(i);
            }
        }

        private bool IsGeneratedFxParameter(string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName))
                return false;

            if (!string.IsNullOrWhiteSpace(curlParameter) &&
                parameterName == curlParameter)
            {
                return true;
            }

            if (parameterName == TrackerControlParameter ||
                parameterName == TrackerSizeParameter)
            {
                return true;
            }

            return parameterName.StartsWith("TailSeek", StringComparison.OrdinalIgnoreCase) ||
                   parameterName.StartsWith("ContactTracker/", StringComparison.Ordinal);
        }

        private void RemoveGeneratedExpressionParameters()
        {
            if (avatar == null ||
                avatar.expressionParameters == null)
            {
                return;
            }

            VRCExpressionParameters expressions = avatar.expressionParameters;
            if (expressions.parameters == null)
                return;

            List<VRCExpressionParameters.Parameter> kept =
                new List<VRCExpressionParameters.Parameter>();

            bool changed = false;
            foreach (VRCExpressionParameters.Parameter parameter in expressions.parameters)
            {
                if (parameter != null &&
                    IsGeneratedFxParameter(parameter.name))
                {
                    changed = true;
                    continue;
                }

                kept.Add(parameter);
            }

            if (!changed)
                return;

            Undo.RecordObject(expressions, "Remove Tail Seek Expression Parameters");
            expressions.parameters = kept.ToArray();
            EditorUtility.SetDirty(expressions);
        }

        private static int GetDepth(Transform transform)
        {
            int depth = 0;
            Transform current = transform;
            while (current != null)
            {
                depth++;
                current = current.parent;
            }

            return depth;
        }

        private void RemoveGeneratedCurlLayer(
            AnimatorController controller)
        {
            RemoveLayerIfPresent(
                controller,
                CurlLayerName);

            RemoveLayerIfPresent(
                controller,
                CurlRemapLayerName);

            RemoveLayerIfPresent(
                controller,
                DirectionLayerName);

            RemoveLayerIfPresent(
                controller,
                SeekLayerName);
        }

        private void RemoveLayerIfPresent(
            AnimatorController controller,
            string layerName)
        {
            for (int i =
                    controller.layers.Length - 1;
                 i >= 0;
                 i--)
            {
                if (controller.layers[i].name ==
                    layerName)
                {
                    controller.RemoveLayer(i);
                }
            }
        }

        // =====================================================================
        // HIERARCHY HELPERS
        // =====================================================================

        private Transform FindChildRecursiveIgnoreCase(
            Transform parent,
            string childName)
        {
            if (parent == null ||
                string.IsNullOrEmpty(childName))
            {
                return null;
            }

            foreach (Transform child in parent)
            {
                if (string.Equals(
                    child.name,
                    childName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                Transform result =
                    FindChildRecursiveIgnoreCase(
                        child,
                        childName);

                if (result != null)
                    return result;
            }

            return null;
        }

        private Transform FindChildRecursive(
            Transform parent,
            string childName)
        {
            if (parent == null)
                return null;

            foreach (Transform child in parent)
            {
                if (child.name == childName)
                    return child;

                Transform result =
                    FindChildRecursive(
                        child,
                        childName);

                if (result != null)
                    return result;
            }

            return null;
        }

        private Transform FindOrCreateChild(
            Transform parent,
            string name)
        {
            Transform existing =
                parent.Find(name);

            if (existing != null)
                return existing;

            GameObject obj =
                new GameObject(name);

            Undo.RegisterCreatedObjectUndo(
                obj,
                "Create " + name);

            obj.transform.SetParent(
                parent,
                false);

            return obj.transform;
        }

        // =====================================================================
        // CONTACT TRACKER
        // =====================================================================

        private void RemoveTrackerDemoContainer(Transform trackerRoot)
        {
            if (trackerRoot == null)
                return;

            Transform container = trackerRoot.Find("Container");
            if (container == null)
                container = FindChildRecursive(trackerRoot, "Container");

            if (container != null)
            {
                Undo.DestroyObjectImmediate(container.gameObject);
                return;
            }

            Transform cube = trackerRoot.Find("Cube");
            if (cube == null)
                cube = FindChildRecursive(trackerRoot, "Cube");

            if (cube != null)
                Undo.DestroyObjectImmediate(cube.gameObject);
        }

        private void ConfigureTrackerReceivers(
            Transform trackerRoot)
        {
            VRCContactReceiver[] receivers =
                trackerRoot.GetComponentsInChildren<
                    VRCContactReceiver>(
                    true);

            int configured =
                0;

            foreach (VRCContactReceiver receiver
                in receivers)
            {
                string objectName =
                    receiver.gameObject.name;

                if (!IsTrackerPoint(
                    objectName))
                {
                    continue;
                }

                receiver.allowSelf =
                    allowSelf;

                receiver.allowOthers =
                    allowOthers;

                receiver.localOnly =
                    false;

                receiver.receiverType =
                    VRCContactReceiver
                        .ReceiverType
                        .Proximity;

                receiver.parameter =
                    "ContactTracker/" +
                    objectName;

                receiver.radius =
                    trackerSize;

                receiver.collisionTags.Clear();

                receiver.collisionTags.Add(
                    collisionTag);

                receiver.ApplyConfigurationChanges();

                EditorUtility.SetDirty(
                    receiver);

                configured++;
            }

            if (configured != 6)
            {
                Debug.LogWarning(
                    "Tail Seek found " +
                    configured +
                    " tracker receivers instead of 6. " +
                    "The VRLabs prefab should contain X+, X-, Y+, Y-, Z+, Z-.");
            }
        }

        private bool IsTrackerPoint(
            string name)
        {
            return name == "X+" ||
                   name == "X-" ||
                   name == "Y+" ||
                   name == "Y-" ||
                   name == "Z+" ||
                   name == "Z-";
        }

        private void ConfigureSender(
            VRCContactSender sender)
        {
            sender.localOnly =
                false;

            sender.radius =
                senderRadius;

            sender.collisionTags.Clear();

            sender.collisionTags.Add(
                collisionTag);

            sender.ApplyConfigurationChanges();

            EditorUtility.SetDirty(
                sender);
        }

        private void ConfigureCurlReceiver(
            VRCContactReceiver receiver)
        {
            receiver.allowSelf =
                allowSelf;

            receiver.allowOthers =
                allowOthers;

            receiver.localOnly =
                false;

            receiver.receiverType =
                VRCContactReceiver
                    .ReceiverType
                    .Proximity;

            receiver.parameter =
                curlParameter;

            receiver.radius =
                curlDistance;

            receiver.collisionTags.Clear();

            receiver.collisionTags.Add(
                collisionTag);

            receiver.ApplyConfigurationChanges();

            EditorUtility.SetDirty(
                receiver);
        }

        // =====================================================================
        // FX CONTROLLER
        // =====================================================================

        private AnimatorController GetFXController()
        {
            if (avatar == null ||
                avatar.baseAnimationLayers == null)
            {
                return null;
            }

            if (avatar.baseAnimationLayers.Length < 5)
                return null;

            return avatar
                .baseAnimationLayers[4]
                .animatorController
                as AnimatorController;
        }

        // =====================================================================
        // MERGE VRLABS TRACKER FX
        // =====================================================================

        private void MergeTrackerFXController(
            AnimatorController target,
            AnimatorController source)
        {
            if (target == null)
            {
                throw new Exception(
                    "Avatar FX controller is missing.");
            }

            if (source == null)
            {
                throw new Exception(
                    "VRLabs Contact Tracker FX controller is missing.");
            }

            if (target == source)
                return;

            // -------------------------------------------------------------
            // Parameters
            // -------------------------------------------------------------

            foreach (AnimatorControllerParameter parameter
                in source.parameters)
            {
                AnimatorControllerParameter existing =
                    FindParameter(
                        target,
                        parameter.name);

                if (existing == null)
                {
                    AnimatorControllerParameter clone =
                        new AnimatorControllerParameter
                        {
                            name = parameter.name,
                            type = parameter.type,
                            defaultBool = parameter.defaultBool,
                            defaultFloat = parameter.defaultFloat,
                            defaultInt = parameter.defaultInt
                        };

                    target.AddParameter(
                        clone);
                }
                else if (existing.type != parameter.type)
                {
                    throw new Exception(
                        "The avatar FX controller already contains parameter '" +
                        parameter.name +
                        "' with a different type (" +
                        existing.type +
                        " instead of " +
                        parameter.type +
                        ").");
                }
            }

            // -------------------------------------------------------------
            // Layers
            // -------------------------------------------------------------

            foreach (AnimatorControllerLayer sourceLayer
                in source.layers)
            {
                // The builder owns these two VRLabs layers.
                // Remove an older copy before adding the fresh copy.
                if (sourceLayer.name ==
                        "Contact Tracker Control" ||
                    sourceLayer.name ==
                        "Contact Tracker Blend Tree")
                {
                    RemoveLayerIfPresent(
                        target,
                        sourceLayer.name);
                }

                AnimatorControllerLayer clonedLayer =
                    CloneLayerIntoController(
                        target,
                        sourceLayer);

                target.AddLayer(
                    clonedLayer);
            }

            PersistLayerDefaultWeight(
                target,
                "Contact Tracker Control",
                1.0f);

            PersistLayerDefaultWeight(
                target,
                "Contact Tracker Blend Tree",
                1.0f);

            EditorUtility.SetDirty(
                target);
        }

        private AnimatorControllerParameter FindParameter(
            AnimatorController controller,
            string name)
        {
            foreach (AnimatorControllerParameter parameter
                in controller.parameters)
            {
                if (parameter.name == name)
                    return parameter;
            }

            return null;
        }

        private AnimatorControllerLayer CloneLayerIntoController(
            AnimatorController target,
            AnimatorControllerLayer sourceLayer)
        {
            string uniqueName =
                sourceLayer.name;

            int suffix =
                1;

            while (HasLayer(
                target,
                uniqueName))
            {
                uniqueName =
                    sourceLayer.name +
                    " " +
                    suffix;

                suffix++;
            }

            Dictionary<AnimatorState, AnimatorState>
                stateMap =
                    new Dictionary<
                        AnimatorState,
                        AnimatorState>();

            Dictionary<AnimatorStateMachine, AnimatorStateMachine>
                stateMachineMap =
                    new Dictionary<
                        AnimatorStateMachine,
                        AnimatorStateMachine>();

            string controllerPath =
                AssetDatabase.GetAssetPath(
                    target);

            AnimatorStateMachine clonedStateMachine =
                CloneStateMachine(
                    sourceLayer.stateMachine,
                    controllerPath,
                    stateMap,
                    stateMachineMap);

            AnimatorControllerLayer layer =
                new AnimatorControllerLayer
                {
                    name = uniqueName,
                    avatarMask = sourceLayer.avatarMask,
                    blendingMode = sourceLayer.blendingMode,
                    defaultWeight = sourceLayer.defaultWeight,
                    iKPass = sourceLayer.iKPass,
                    syncedLayerIndex = -1,
                    syncedLayerAffectsTiming =
                        sourceLayer.syncedLayerAffectsTiming,
                    stateMachine =
                        clonedStateMachine
                };

            return layer;
        }

        private bool HasLayer(
            AnimatorController controller,
            string name)
        {
            foreach (AnimatorControllerLayer layer
                in controller.layers)
            {
                if (layer.name == name)
                    return true;
            }

            return false;
        }

        // =====================================================================
        // STATE MACHINE CLONING
        // =====================================================================

        private AnimatorStateMachine CloneStateMachine(
            AnimatorStateMachine source,
            string controllerPath,
            Dictionary<AnimatorState, AnimatorState> stateMap,
            Dictionary<AnimatorStateMachine, AnimatorStateMachine> stateMachineMap)
        {
            if (source == null)
                return null;

            if (stateMachineMap.ContainsKey(
                source))
            {
                return stateMachineMap[
                    source];
            }

            AnimatorStateMachine destination =
                new AnimatorStateMachine();

            destination.name =
                source.name;

            destination.hideFlags =
                HideFlags.HideInHierarchy;

            if (!string.IsNullOrEmpty(
                controllerPath))
            {
                AssetDatabase.AddObjectToAsset(
                    destination,
                    controllerPath);
            }

            stateMachineMap[
                source] =
                destination;

            destination.anyStatePosition =
                source.anyStatePosition;

            destination.entryPosition =
                source.entryPosition;

            destination.exitPosition =
                source.exitPosition;

            destination.parentStateMachinePosition =
                source.parentStateMachinePosition;

            // -------------------------------------------------------------
            // States
            // -------------------------------------------------------------

            foreach (ChildAnimatorState childState
                in source.states)
            {
                AnimatorState added =
                    destination.AddState(
                        childState.state.name,
                        childState.position);

                CopyAnimatorStateData(
                    childState.state,
                    added,
                    controllerPath);

                stateMap[
                    childState.state] =
                    added;
            }

            // -------------------------------------------------------------
            // Child state machines
            // -------------------------------------------------------------

            foreach (ChildAnimatorStateMachine childMachine
                in source.stateMachines)
            {
                AnimatorStateMachine clonedChild =
                    CloneStateMachine(
                        childMachine.stateMachine,
                        controllerPath,
                        stateMap,
                        stateMachineMap);

                destination.AddStateMachine(
                    clonedChild,
                    childMachine.position);
            }

            destination.defaultState =
                source.defaultState != null &&
                stateMap.ContainsKey(
                    source.defaultState)
                    ? stateMap[
                        source.defaultState]
                    : null;

            // -------------------------------------------------------------
            // Transitions
            // -------------------------------------------------------------

            CloneAnyStateTransitions(
                source,
                destination,
                stateMap);

            CloneEntryTransitions(
                source,
                destination,
                stateMap);

            CloneStateTransitions(
                source,
                destination,
                stateMap);

            CloneStateMachineTransitions(
                source,
                destination,
                stateMachineMap,
                stateMap);

            return destination;
        }

        private void CopyAnimatorStateData(
            AnimatorState source,
            AnimatorState destination,
            string controllerPath)
        {
            destination.name =
                source.name;

            destination.hideFlags =
                HideFlags.HideInHierarchy;

            destination.speed =
                source.speed;

            destination.cycleOffset =
                source.cycleOffset;

            destination.mirror =
                source.mirror;

            destination.iKOnFeet =
                source.iKOnFeet;

            destination.writeDefaultValues =
                source.writeDefaultValues;

            destination.speedParameter =
                source.speedParameter;

            destination.speedParameterActive =
                source.speedParameterActive;

            destination.cycleOffsetParameter =
                source.cycleOffsetParameter;

            destination.cycleOffsetParameterActive =
                source.cycleOffsetParameterActive;

            destination.mirrorParameter =
                source.mirrorParameter;

            destination.mirrorParameterActive =
                source.mirrorParameterActive;

            destination.timeParameter =
                source.timeParameter;

            destination.timeParameterActive =
                source.timeParameterActive;

            destination.tag =
                source.tag;

            if (source.motion is BlendTree sourceTree)
            {
                destination.motion =
                    CloneBlendTree(
                        sourceTree,
                        controllerPath);
            }
            else
            {
                destination.motion =
                    source.motion;
            }
        }

        private BlendTree CloneBlendTree(
            BlendTree source,
            string controllerPath)
        {
            if (source == null)
                return null;

            BlendTree destination =
                new BlendTree();

            EditorUtility.CopySerialized(
                source,
                destination);

            destination.name =
                source.name;

            destination.hideFlags =
                HideFlags.HideInHierarchy;

            if (!string.IsNullOrEmpty(
                controllerPath))
            {
                AssetDatabase.AddObjectToAsset(
                    destination,
                    controllerPath);
            }

            ChildMotion[] children =
                source.children;

            for (int i = 0;
                 i < children.Length;
                 i++)
            {
                if (children[i].motion
                    is BlendTree childTree)
                {
                    children[i].motion =
                        CloneBlendTree(
                            childTree,
                            controllerPath);
                }
            }

            destination.children =
                children;

            return destination;
        }

        private void CloneAnyStateTransitions(
            AnimatorStateMachine source,
            AnimatorStateMachine destination,
            Dictionary<AnimatorState, AnimatorState> stateMap)
        {
            foreach (AnimatorStateTransition transition
                in source.anyStateTransitions)
            {
                if (transition.destinationState == null)
                    continue;

                if (!stateMap.ContainsKey(
                    transition.destinationState))
                {
                    continue;
                }

                AnimatorStateTransition clone =
                    destination.AddAnyStateTransition(
                        stateMap[
                            transition.destinationState]);

                CopyStateTransition(
                    transition,
                    clone);
            }
        }

        private void CloneEntryTransitions(
            AnimatorStateMachine source,
            AnimatorStateMachine destination,
            Dictionary<AnimatorState, AnimatorState> stateMap)
        {
            foreach (AnimatorTransition transition
                in source.entryTransitions)
            {
                if (transition.destinationState == null)
                    continue;

                if (!stateMap.ContainsKey(
                    transition.destinationState))
                {
                    continue;
                }

                AnimatorTransition clone =
                    destination.AddEntryTransition(
                        stateMap[
                            transition.destinationState]);

                CopyAnimatorTransition(
                    transition,
                    clone);
            }
        }

        private void CloneStateTransitions(
            AnimatorStateMachine source,
            AnimatorStateMachine destination,
            Dictionary<AnimatorState, AnimatorState> stateMap)
        {
            foreach (ChildAnimatorState childState
                in source.states)
            {
                if (!stateMap.ContainsKey(
                    childState.state))
                {
                    continue;
                }

                AnimatorState destinationState =
                    stateMap[
                        childState.state];

                foreach (AnimatorStateTransition transition
                    in childState.state.transitions)
                {
                    if (transition.destinationState != null &&
                        stateMap.ContainsKey(
                            transition.destinationState))
                    {
                        AnimatorStateTransition clone =
                            destinationState.AddTransition(
                                stateMap[
                                    transition.destinationState]);

                        CopyStateTransition(
                            transition,
                            clone);
                    }
                    else if (transition.isExit)
                    {
                        AnimatorStateTransition clone =
                            destinationState.AddExitTransition();

                        CopyStateTransition(
                            transition,
                            clone);
                    }
                }
            }
        }

        private void CloneStateMachineTransitions(
            AnimatorStateMachine source,
            AnimatorStateMachine destination,
            Dictionary<AnimatorStateMachine, AnimatorStateMachine> stateMachineMap,
            Dictionary<AnimatorState, AnimatorState> stateMap)
        {
            foreach (ChildAnimatorStateMachine child
                in source.stateMachines)
            {
                if (!stateMachineMap.ContainsKey(
                    child.stateMachine))
                {
                    continue;
                }

                AnimatorStateMachine childDestination =
                    stateMachineMap[
                        child.stateMachine];

                foreach (AnimatorTransition transition
                    in source.GetStateMachineTransitions(
                        child.stateMachine))
                {
                    AnimatorTransition clone =
                        null;

                    if (transition.destinationState != null &&
                        stateMap.ContainsKey(
                            transition.destinationState))
                    {
                        clone =
                            destination.AddStateMachineTransition(
                                childDestination,
                                stateMap[
                                    transition.destinationState]);
                    }
                    else if (
                        transition.destinationStateMachine != null &&
                        stateMachineMap.ContainsKey(
                            transition.destinationStateMachine))
                    {
                        clone =
                            destination.AddStateMachineTransition(
                                childDestination,
                                stateMachineMap[
                                    transition.destinationStateMachine]);
                    }
                    else if (transition.isExit)
                    {
                        clone =
                            destination.AddStateMachineExitTransition(
                                childDestination);
                    }

                    if (clone != null)
                    {
                        CopyAnimatorTransition(
                            transition,
                            clone);
                    }
                }
            }
        }

        private void CopyStateTransition(
            AnimatorStateTransition source,
            AnimatorStateTransition destination)
        {
            destination.canTransitionToSelf =
                source.canTransitionToSelf;

            destination.duration =
                source.duration;

            destination.exitTime =
                source.exitTime;

            destination.hasExitTime =
                source.hasExitTime;

            destination.hasFixedDuration =
                source.hasFixedDuration;

            destination.interruptionSource =
                source.interruptionSource;

            destination.orderedInterruption =
                source.orderedInterruption;

            destination.offset =
                source.offset;

            destination.mute =
                source.mute;

            destination.solo =
                source.solo;

            destination.isExit =
                source.isExit;

            foreach (AnimatorCondition condition
                in source.conditions)
            {
                destination.AddCondition(
                    condition.mode,
                    condition.threshold,
                    condition.parameter);
            }
        }

        private void CopyAnimatorTransition(
            AnimatorTransition source,
            AnimatorTransition destination)
        {
            destination.mute =
                source.mute;

            destination.solo =
                source.solo;

            destination.isExit =
                source.isExit;

            foreach (AnimatorCondition condition
                in source.conditions)
            {
                destination.AddCondition(
                    condition.mode,
                    condition.threshold,
                    condition.parameter);
            }
        }

        // =====================================================================
        // CURL FX LAYER
        // =====================================================================

        private string CurlTimeParameterName
        {
            get { return curlParameter + "_Time"; }
        }

        private void AddCurlParameter(
            AnimatorController controller)
        {
            AddFloatParameterIfMissing(
                controller,
                curlParameter);

            AddFloatParameterIfMissing(
                controller,
                CurlTimeParameterName);
        }

        private void AddFloatParameterIfMissing(
            AnimatorController controller,
            string parameterName)
        {
            AnimatorControllerParameter existing =
                FindParameter(
                    controller,
                    parameterName);

            if (existing != null)
            {
                if (existing.type !=
                    AnimatorControllerParameterType.Float)
                {
                    throw new Exception(
                        "Animator parameter '" +
                        parameterName +
                        "' already exists but is not a Float.");
                }

                return;
            }

            AnimatorControllerParameter parameter =
                new AnimatorControllerParameter
                {
                    name = parameterName,
                    type =
                        AnimatorControllerParameterType.Float,
                    defaultFloat = 0f
                };

            controller.AddParameter(
                parameter);

            EditorUtility.SetDirty(
                controller);
        }

        private string SeekMotionTimeParameter
        {
            get
            {
                if (addCurlAnimator &&
                    fullCurlDistance + 0.0001f < curlDistance)
                {
                    return CurlTimeParameterName;
                }

                return curlParameter;
            }
        }

        private bool UsesCurlTimeRemap
        {
            get
            {
                return addCurlAnimator &&
                       fullCurlDistance + 0.0001f < curlDistance;
            }
        }

        private void AddSeekLayer(
            AnimatorController controller)
        {
            RemoveGeneratedCurlLayer(
                controller);

            string controllerPath =
                AssetDatabase.GetAssetPath(
                    controller);

            if (UsesCurlTimeRemap)
            {
                AddCurlRemapLayer(
                    controller,
                    controllerPath);
            }

            AddFloatParameterIfMissing(
                controller,
                TrackerLeftParameter);

            AddFloatParameterIfMissing(
                controller,
                TrackerRightParameter);

            AnimatorControllerLayer layer =
                new AnimatorControllerLayer
                {
                    name = SeekLayerName,
                    defaultWeight = 1.0f,
                    blendingMode =
                        AnimatorLayerBlendingMode.Override,
                    stateMachine =
                        new AnimatorStateMachine()
                };

            layer.stateMachine.name = SeekLayerName;

            if (!string.IsNullOrEmpty(controllerPath))
            {
                AssetDatabase.AddObjectToAsset(
                    layer.stateMachine,
                    controllerPath);
            }

            controller.AddLayer(layer);
            PersistLayerDefaultWeight(
                controller,
                SeekLayerName,
                1.0f);

            AnimatorStateMachine machine =
                GetLayerStateMachine(
                    controller,
                    SeekLayerName);

            if (machine == null)
            {
                throw new Exception(
                    "Unity did not keep the Tail Seek layer state machine.");
            }

            bool hasWrap = addCurlAnimator && curlAnimation != null;
            bool hasDirection = addDirectionalCurl &&
                leftCurlAnimation != null &&
                rightCurlAnimation != null;

            AnimatorState idleState = null;
            AnimatorState wrapState = null;
            AnimatorState leftState = null;
            AnimatorState rightState = null;

            if (hasDirection || !hasWrap)
            {
                idleState = CreateSeekState(
                    machine,
                    "Idle",
                    CreateEmptyClip(controller),
                    new Vector3(250, 0, 0),
                    false);
            }

            if (hasWrap)
            {
                wrapState = CreateSeekState(
                    machine,
                    "Wrap",
                    curlAnimation,
                    new Vector3(250, 120, 0),
                    true);

                if (wrapState.motion == null)
                {
                    throw new Exception(
                        "The wrapping animation was not saved onto the Wrap state. " +
                        "Make sure the Tail Wrapping Animation field is assigned.");
                }
            }

            if (hasDirection)
            {
                leftState = CreateSeekState(
                    machine,
                    "Left Curl",
                    leftCurlAnimation,
                    new Vector3(40, 120, 0),
                    true);

                rightState = CreateSeekState(
                    machine,
                    "Right Curl",
                    rightCurlAnimation,
                    new Vector3(460, 120, 0),
                    true);
            }

            if (hasWrap && !hasDirection)
            {
                machine.defaultState = wrapState;
            }
            else if (idleState != null)
            {
                machine.defaultState = idleState;
            }
            else if (wrapState != null)
            {
                machine.defaultState = wrapState;
            }

            if (hasWrap && hasDirection)
            {
                WireWrapAndDirectionTransitions(
                    idleState,
                    wrapState,
                    leftState,
                    rightState);
            }
            else if (hasDirection)
            {
                WireDirectionOnlyTransitions(
                    idleState,
                    leftState,
                    rightState);
            }

            PersistLayerDefaultWeight(
                controller,
                SeekLayerName,
                1.0f);

            EditorUtility.SetDirty(machine);
            EditorUtility.SetDirty(controller);
        }

        private AnimatorState CreateSeekState(
            AnimatorStateMachine machine,
            string stateName,
            Motion motion,
            Vector3 position,
            bool useMotionTime)
        {
            AnimatorState state = machine.AddState(stateName, position);
            state.motion = motion;
            state.writeDefaultValues = false;
            state.speed = 1.0f;
            state.speedParameterActive = false;

            if (useMotionTime)
            {
                state.timeParameterActive = true;
                state.timeParameter = SeekMotionTimeParameter;
            }

            return state;
        }

        private void WireWrapAndDirectionTransitions(
            AnimatorState idle,
            AnimatorState wrap,
            AnimatorState left,
            AnimatorState right)
        {
            const float sideOn = 0.12f;
            const float sideOff = 0.05f;
            const float wrapOn = 0.04f;
            const float wrapOff = 0.02f;

            AddTransition(
                idle,
                right,
                Greater(TrackerRightParameter, sideOn));
            AddTransition(
                idle,
                left,
                Greater(TrackerLeftParameter, sideOn));
            AddTransition(
                idle,
                wrap,
                Greater(curlParameter, wrapOn));

            AddTransition(
                wrap,
                right,
                Greater(TrackerRightParameter, sideOn));
            AddTransition(
                wrap,
                left,
                Greater(TrackerLeftParameter, sideOn));
            AddTransition(
                wrap,
                idle,
                Less(curlParameter, wrapOff));

            AddTransition(
                left,
                right,
                Greater(TrackerRightParameter, sideOn));
            AddTransition(
                left,
                wrap,
                Less(TrackerLeftParameter, sideOff),
                Greater(curlParameter, wrapOn));
            AddTransition(
                left,
                idle,
                Less(TrackerLeftParameter, sideOff),
                Less(curlParameter, wrapOff));

            AddTransition(
                right,
                left,
                Greater(TrackerLeftParameter, sideOn),
                Less(TrackerRightParameter, sideOn));
            AddTransition(
                right,
                wrap,
                Less(TrackerRightParameter, sideOff),
                Greater(curlParameter, wrapOn));
            AddTransition(
                right,
                idle,
                Less(TrackerRightParameter, sideOff),
                Less(curlParameter, wrapOff));
        }

        private void WireDirectionOnlyTransitions(
            AnimatorState idle,
            AnimatorState left,
            AnimatorState right)
        {
            const float sideOn = 0.12f;
            const float sideOff = 0.05f;

            AddTransition(
                idle,
                right,
                Greater(TrackerRightParameter, sideOn));
            AddTransition(
                idle,
                left,
                Greater(TrackerLeftParameter, sideOn));
            AddTransition(
                left,
                right,
                Greater(TrackerRightParameter, sideOn));
            AddTransition(
                left,
                idle,
                Less(TrackerLeftParameter, sideOff));
            AddTransition(
                right,
                left,
                Greater(TrackerLeftParameter, sideOn),
                Less(TrackerRightParameter, sideOn));
            AddTransition(
                right,
                idle,
                Less(TrackerRightParameter, sideOff));
        }

        private static AnimatorCondition Greater(string parameter, float threshold)
        {
            return new AnimatorCondition
            {
                mode = AnimatorConditionMode.Greater,
                parameter = parameter,
                threshold = threshold
            };
        }

        private static AnimatorCondition Less(string parameter, float threshold)
        {
            return new AnimatorCondition
            {
                mode = AnimatorConditionMode.Less,
                parameter = parameter,
                threshold = threshold
            };
        }

        private static void AddTransition(
            AnimatorState from,
            AnimatorState to,
            params AnimatorCondition[] conditions)
        {
            AnimatorStateTransition transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.exitTime = 0f;
            transition.canTransitionToSelf = false;
            transition.interruptionSource = TransitionInterruptionSource.Source;

            for (int i = 0; i < conditions.Length; i++)
            {
                transition.AddCondition(
                    conditions[i].mode,
                    conditions[i].threshold,
                    conditions[i].parameter);
            }
        }

        private void AddCurlRemapLayer(
            AnimatorController controller,
            string controllerPath)
        {
            RemoveLayerIfPresent(
                controller,
                CurlRemapLayerName);

            AnimatorControllerLayer layer =
                new AnimatorControllerLayer
                {
                    name = CurlRemapLayerName,
                    defaultWeight = 1.0f,
                    blendingMode =
                        AnimatorLayerBlendingMode.Override,
                    stateMachine =
                        new AnimatorStateMachine()
                };

            layer.stateMachine.name =
                CurlRemapLayerName;

            layer.stateMachine.hideFlags =
                HideFlags.HideInHierarchy;

            if (!string.IsNullOrEmpty(controllerPath))
            {
                AssetDatabase.AddObjectToAsset(
                    layer.stateMachine,
                    controllerPath);
            }

            controller.AddLayer(layer);
            PersistLayerDefaultWeight(
                controller,
                CurlRemapLayerName,
                1.0f);

            AnimatorStateMachine machine =
                GetLayerStateMachine(
                    controller,
                    CurlRemapLayerName);

            if (machine == null)
            {
                throw new Exception(
                    "Unity did not keep the Tail Seek curl remap layer.");
            }

            BlendTree tree =
                new BlendTree
                {
                    name = "Tail Seek Curl Time",
                    blendType = BlendTreeType.Simple1D,
                    blendParameter = curlParameter,
                    useAutomaticThresholds = false,
                    hideFlags = HideFlags.HideInHierarchy
                };

            if (!string.IsNullOrEmpty(controllerPath))
                AssetDatabase.AddObjectToAsset(tree, controllerPath);

            AnimationClip restClip =
                CreateAnimatorFloatClip(
                    controller,
                    CurlTimeParameterName,
                    0f,
                    "TailSeek_CurlTime_Rest");

            AnimationClip fullClip =
                CreateAnimatorFloatClip(
                    controller,
                    CurlTimeParameterName,
                    1f,
                    "TailSeek_CurlTime_Full");

            float fullThreshold =
                FullCurlProximityThreshold();

            tree.AddChild(restClip, 0f);
            tree.AddChild(fullClip, fullThreshold);
            if (fullThreshold < 0.999f)
                tree.AddChild(fullClip, 1f);

            AnimatorState remapState =
                machine.AddState(
                    "Curl Time Remap",
                    new Vector3(250, 0, 0));

            remapState.hideFlags = HideFlags.HideInHierarchy;
            remapState.motion = tree;
            remapState.writeDefaultValues = false;
            machine.defaultState = remapState;

            PersistLayerDefaultWeight(
                controller,
                CurlRemapLayerName,
                1.0f);

            EditorUtility.SetDirty(remapState);
            EditorUtility.SetDirty(machine);
            EditorUtility.SetDirty(controller);
        }

        private float FullCurlProximityThreshold()
        {
            if (curlDistance <= 0f)
                return 1f;

            if (fullCurlDistance >= curlDistance)
                return 0.0001f;

            return Mathf.Clamp01(1f - fullCurlDistance / curlDistance);
        }

        private AnimationClip CreateAnimatorFloatClip(
            AnimatorController controller,
            string parameterName,
            float value,
            string clipName)
        {
            string controllerPath =
                AssetDatabase.GetAssetPath(controller);

            string directory =
                Path.GetDirectoryName(controllerPath);

            if (string.IsNullOrEmpty(directory))
                directory = "Assets";

            directory = directory.Replace("\\", "/");

            string path = directory + "/" + clipName + ".anim";

            AnimationClip clip =
                AssetDatabase.LoadAssetAtPath<AnimationClip>(path);

            if (clip == null)
            {
                clip = new AnimationClip
                {
                    name = clipName,
                    frameRate = 60f
                };

                AssetDatabase.CreateAsset(clip, path);
            }

            EditorCurveBinding binding =
                new EditorCurveBinding
                {
                    path = "",
                    type = typeof(Animator),
                    propertyName = parameterName
                };

            AnimationCurve curve =
                AnimationCurve.Constant(0f, 0f, value);

            AnimationUtility.SetEditorCurve(clip, binding, curve);
            EditorUtility.SetDirty(clip);
            return clip;
        }

        private static AnimatorStateMachine GetLayerStateMachine(
            AnimatorController controller,
            string layerName)
        {
            if (controller == null ||
                string.IsNullOrEmpty(layerName))
            {
                return null;
            }

            AnimatorControllerLayer[] layers =
                controller.layers;

            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].name == layerName)
                    return layers[i].stateMachine;
            }

            return null;
        }

        /// <summary>
        /// Sets layer weight through SerializedObject. Assigning controller.layers
        /// back from a copied array can strip state motions and produce
        /// "(NO VALID ANIMATIONS)" in the Animator window.
        /// </summary>
        private static void PersistLayerDefaultWeight(
            AnimatorController controller,
            string layerName,
            float weight)
        {
            if (controller == null ||
                string.IsNullOrEmpty(layerName))
            {
                return;
            }

            SerializedObject serialized =
                new SerializedObject(
                    controller);

            SerializedProperty layers =
                serialized.FindProperty(
                    "m_AnimatorLayers");

            if (layers == null ||
                !layers.isArray)
            {
                return;
            }

            for (int i = 0; i < layers.arraySize; i++)
            {
                SerializedProperty layer =
                    layers.GetArrayElementAtIndex(
                        i);

                SerializedProperty nameProperty =
                    layer.FindPropertyRelative(
                        "m_Name");

                if (nameProperty == null ||
                    nameProperty.stringValue != layerName)
                {
                    continue;
                }

                SerializedProperty weightProperty =
                    layer.FindPropertyRelative(
                        "m_DefaultWeight");

                if (weightProperty != null)
                    weightProperty.floatValue = weight;
            }

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(
                controller);
        }

        // =====================================================================
        // EMPTY CLIP
        // =====================================================================

        private AnimationClip CreateEmptyClip(
            AnimatorController controller)
        {
            string controllerPath =
                AssetDatabase.GetAssetPath(
                    controller);

            string directory =
                Path.GetDirectoryName(
                    controllerPath);

            if (string.IsNullOrEmpty(
                directory))
            {
                directory =
                    "Assets";
            }

            directory =
                directory.Replace(
                    "\\",
                    "/");

            string path =
                directory +
                "/TailSeek_Empty.anim";

            AnimationClip existing =
                AssetDatabase.LoadAssetAtPath<
                    AnimationClip>(
                        path);

            if (existing != null)
                return existing;

            path =
                AssetDatabase.GenerateUniqueAssetPath(
                    path);

            AnimationClip clip =
                new AnimationClip();

            clip.name =
                "TailSeek_Empty";

            AssetDatabase.CreateAsset(
                clip,
                path);

            return clip;
        }
    }
}

#endif
