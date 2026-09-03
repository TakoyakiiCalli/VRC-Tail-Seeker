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
    /// - The Contact Tracker and curl receiver are placed on the Hip.
    ///   The tail sender stays on the Tail Tip so other avatars can detect this tail.
    /// - The VRLabs "Tracker Target" is moved outside the Contact Tracker, as required by
    ///   the VRLabs installation instructions.
    /// - The supplied Contact Tracker FX controller is merged into the avatar FX controller.
    /// - The user's curl layer is added after the tracker layers.
    /// </summary>
    public class TailSeekerBuilder : EditorWindow
    {
        private const string GeneratedTrackerName = "TailSeek Contact Tracker";
        private const string GeneratedSenderName = "TailSeek Sender";
        private const string GeneratedCurlReceiverName = "TailSeek Curl Receiver";
        private const string GeneratedTargetName = "TailSeek Tracker Target";
        private const string CurlLayerName = "Tail Seek - Curl";

        private const string TrackerControlParameter = "ContactTracker/Control";
        private const string TrackerSizeParameter = "ContactTracker/Size";

        private VRCAvatarDescriptor avatar;

        private Transform tailRoot;
        private Transform tailTip;
        private Transform hip;
        private AnimationClip curlAnimation;

        private GameObject contactTrackerPrefab;
        private AnimatorController contactTrackerController;

        private string collisionTag = "TailSeek";

        // Radius of each of the six VRChat proximity receivers.
        private float trackerSize = 1.0f;
        private float senderRadius = 0.025f;

        private bool allowSelf = false;
        private bool allowOthers = true;

        private float curlDistance = 0.50f;
        private float fullCurlDistance = 0.10f;

        private string curlParameter = "TailSeek_Curl";

        private bool addDirectionalConstraint = true;
        private Vector3 aimAxis = Vector3.forward;
        private Vector3 upAxis = Vector3.up;

        private bool addCurlAnimator = true;
        private bool mergeTrackerFX = true;

        private bool createTestPrefab = true;
        private float testObjectScale = 0.10f;
        private float testMoveSpeed = 1.5f;
        private string testPrefabPath = "Assets/TailSeek/Generated/TailSeek Test Object.prefab";

        private Vector2 scroll;

        [MenuItem("Tools/Tail Seek/Builder")]
        public static void ShowWindow()
        {
            GetWindow<TailSeekerBuilder>("Tail Seek Builder");
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("TAIL SEEK BUILDER", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Builds tail-to-hip interaction using VRChat Contacts and the VRLabs Contact Tracker.\n\n" +
                "The tracker and curl receiver are placed on the Hip. " +
                "The tail sender stays on the Tail Tip so other avatars can detect this tail. " +
                "The VRLabs Tracker Target is placed outside the tracker and used by the Aim Constraint.",
                MessageType.Info);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Avatar", EditorStyles.boldLabel);

            VRCAvatarDescriptor previousAvatar = avatar;

            avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                "Avatar", avatar, typeof(VRCAvatarDescriptor), true);

            if (avatar != previousAvatar ||
                (avatar != null && hip == null))
            {
                hip = FindHipsBone();
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Tail", EditorStyles.boldLabel);

            tailRoot = (Transform)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Tail Root",
                    "First bone of the tail chain. The Aim Constraint is added here."),
                tailRoot, typeof(Transform), true);

            tailTip = (Transform)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Tail Tip",
                    "End of the tail. The TailSeek sender is placed here so other avatars can detect this tail."),
                tailTip, typeof(Transform), true);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Hip", EditorStyles.boldLabel);

            hip = (Transform)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Hip",
                    "Hips/pelvis bone. The Contact Tracker and curl receiver are placed here."),
                hip, typeof(Transform), true);

            if (GUILayout.Button("Find Hips Bone"))
                hip = FindHipsBone();

            curlAnimation = (AnimationClip)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Curl Animation",
                    "Existing tail curl animation."),
                curlAnimation, typeof(AnimationClip), false);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Contact Tracker", EditorStyles.boldLabel);

            contactTrackerPrefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Tracker Prefab",
                    "VRLabs Contact Tracker prefab."),
                contactTrackerPrefab, typeof(GameObject), false);

            contactTrackerController = (AnimatorController)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Tracker FX Controller",
                    "VRLabs Contact Tracker FX controller. Its layers and parameters are merged into the avatar FX controller."),
                contactTrackerController, typeof(AnimatorController), false);

            if (GUILayout.Button("Find VRLabs Tracker Assets"))
                FindTrackerAssets();

            mergeTrackerFX = EditorGUILayout.Toggle(
                new GUIContent(
                    "Merge Tracker FX Controller",
                    "Copies the VRLabs Contact Tracker FX parameters and layers into the avatar FX controller."),
                mergeTrackerFX);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Contact Settings", EditorStyles.boldLabel);

            collisionTag = EditorGUILayout.TextField(
                "Collision Tag",
                collisionTag);

            trackerSize = EditorGUILayout.FloatField(
                new GUIContent(
                    "Tracker Receiver Radius",
                    "Radius of each of the six directional tracker receivers."),
                trackerSize);

            senderRadius = EditorGUILayout.FloatField(
                new GUIContent(
                    "Tail Sender Radius",
                    "Radius of the Contact Sender at the tail tip."),
                senderRadius);

            allowSelf = EditorGUILayout.Toggle(
                "Allow Self",
                allowSelf);

            allowOthers = EditorGUILayout.Toggle(
                "Allow Others",
                allowOthers);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Curl", EditorStyles.boldLabel);

            addCurlAnimator = EditorGUILayout.Toggle(
                "Create Curl Animator",
                addCurlAnimator);

            if (addCurlAnimator)
            {
                EditorGUILayout.HelpBox(
                    "The curl clip is driven like VRCFury Depth Animation: the proximity float " +
                    "scrubs the clip from start to end (Motion Time). Put your four blendshape " +
                    "sets as keyframes along the clip timeline. Frame 0 should be rest, the last " +
                    "frame should be full curl. Do not loop the clip.",
                    MessageType.Info);

                curlDistance = EditorGUILayout.FloatField(
                    new GUIContent(
                        "Curl Start Distance",
                        "Radius of the proximity receiver. Outside this radius the clip is at frame 0."),
                    curlDistance);

                fullCurlDistance = EditorGUILayout.FloatField(
                    new GUIContent(
                        "Full Curl Distance",
                        "Distance at which the clip reaches its last frame. Must be smaller than Curl Start Distance."),
                    fullCurlDistance);

                curlParameter = EditorGUILayout.TextField(
                    "Curl Parameter",
                    curlParameter);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(
                "Directional Seeking",
                EditorStyles.boldLabel);

            addDirectionalConstraint = EditorGUILayout.Toggle(
                new GUIContent(
                    "Aim Tail Toward Target",
                    "Adds an Aim Constraint using the Contact Tracker's Tracker Target."),
                addDirectionalConstraint);

            if (addDirectionalConstraint)
            {
                aimAxis = EditorGUILayout.Vector3Field(
                    new GUIContent(
                        "Tail Forward Axis",
                        "Local axis of the tail root that should point toward the target."),
                    aimAxis);

                upAxis = EditorGUILayout.Vector3Field(
                    "Tail Up Axis",
                    upAxis);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Play Mode Test Object", EditorStyles.boldLabel);

            createTestPrefab = EditorGUILayout.Toggle(
                new GUIContent(
                    "Create Test Prefab",
                    "Creates a visible Contact Sender prefab for testing the Tail Seek collision system."),
                createTestPrefab);

            if (createTestPrefab)
            {
                testObjectScale = EditorGUILayout.FloatField(
                    new GUIContent("Test Object Scale", "Visual diameter/scale of the generated test sphere."),
                    testObjectScale);

                testMoveSpeed = EditorGUILayout.FloatField(
                    new GUIContent("Test Move Speed", "Keyboard movement speed in Play Mode."),
                    testMoveSpeed);

                EditorGUILayout.HelpBox(
                    "After building, the prefab is created at:\n" + testPrefabPath +
                    "\n\nDrag it into the scene, enter Play Mode, then select Gesture Manager.\n" +
                    "Move with W/A/S/D and Q/E (Shift to go faster), or drag it in the Scene view.\n\n" +
                    "The test object simulates another player's TailSeek sender. A scene Contact Sender is not a VRChat player, " +
                    "so it cannot trigger avatar receivers that have Allow Self disabled.",
                    MessageType.None);

                if (GUILayout.Button("CREATE / RECREATE TEST PREFAB"))
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
            }

            EditorGUILayout.Space(15);
            EditorGUILayout.LabelField("Existing System", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Removes leftover Tail Seek objects, contact senders/receivers, aim constraints, " +
                "FX layers, and parameters from previous builds on the assigned avatar, then rebuilds " +
                "with the current settings.",
                MessageType.None);

            bool valid =
                avatar != null &&
                tailRoot != null &&
                tailTip != null &&
                hip != null &&
                contactTrackerPrefab != null &&
                contactTrackerController != null &&
                (!addCurlAnimator || curlAnimation != null);

            GUI.enabled = valid;

            if (GUILayout.Button(
                "REPLACE / REBUILD EXISTING TAIL SEEK SYSTEM",
                GUILayout.Height(32)))
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

            if (GUILayout.Button("REMOVE EXISTING TAIL SEEK SYSTEM"))
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

            GUI.enabled = valid;

            EditorGUILayout.Space(8);

            if (GUILayout.Button(
                "BUILD TAIL SEEK SYSTEM",
                GUILayout.Height(45)))
            {
                Build();
            }

            GUI.enabled = true;

            if (!valid)
            {
                EditorGUILayout.Space(5);

                EditorGUILayout.HelpBox(
                    "Assign Avatar, Tail Root, Tail Tip, Hip, Tracker Prefab, Tracker FX Controller, " +
                    "and Curl Animation when Create Curl Animator is enabled.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(10);

            EditorGUILayout.HelpBox(
                "The builder places the Contact Tracker and curl receiver on the Hip, keeps the sender on the Tail Tip, and merges the " +
                "VRLabs Contact Tracker FX controller into the avatar FX controller.",
                MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        // =====================================================================
        // FIND VRLABS ASSETS
        // =====================================================================

        private void FindTrackerAssets()
        {
            contactTrackerPrefab = null;
            contactTrackerController = null;

            string[] prefabGuids =
                AssetDatabase.FindAssets(
                    "Contact Tracker t:Prefab");

            foreach (string guid in prefabGuids)
            {
                string path =
                    AssetDatabase.GUIDToAssetPath(guid);

                GameObject prefab =
                    AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (prefab != null &&
                    prefab.name == "Contact Tracker")
                {
                    contactTrackerPrefab = prefab;
                    break;
                }
            }

            string[] controllerGuids =
                AssetDatabase.FindAssets(
                    "Contact Tracker FX t:AnimatorController");

            foreach (string guid in controllerGuids)
            {
                string path =
                    AssetDatabase.GUIDToAssetPath(guid);

                AnimatorController controller =
                    AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

                if (controller != null &&
                    controller.name == "Contact Tracker FX")
                {
                    contactTrackerController = controller;
                    break;
                }
            }

            Repaint();

            if (contactTrackerPrefab == null ||
                contactTrackerController == null)
            {
                EditorUtility.DisplayDialog(
                    "Tracker Assets Not Found",
                    "The VRLabs Contact Tracker assets could not be found automatically.\n\n" +
                    "Assign the prefab and FX controller manually.",
                    "OK");
            }
        }

        private Transform FindHipsBone()
        {
            if (avatar == null)
                return null;

            Animator animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
            {
                Transform humanHips =
                    animator.GetBoneTransform(HumanBodyBones.Hips);

                if (humanHips != null)
                    return humanHips;
            }

            Transform namedHips =
                FindChildRecursive(avatar.transform, "Hips");

            if (namedHips != null)
                return namedHips;

            Transform namedHip =
                FindChildRecursive(avatar.transform, "Hip");

            if (namedHip != null)
                return namedHip;

            return FindChildRecursive(avatar.transform, "Pelvis");
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
                // AIM CONSTRAINT
                // -------------------------------------------------------------

                if (addDirectionalConstraint)
                {
                    CreateAimConstraint(
                        tailRoot,
                        trackerTarget);
                }

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

                if (mergeTrackerFX)
                {
                    MergeTrackerFXController(
                        fxController,
                        contactTrackerController);
                }

                if (addCurlAnimator)
                {
                    AddCurlParameter(
                        fxController);

                    AddCurlLayer(
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
                    CreateTestObjectPrefab();
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }

                Selection.activeGameObject =
                    trackerObject;

                EditorUtility.DisplayDialog(
                    "Tail Seek Built",
                    "Tail Seek was successfully generated.\n\n" +
                    "Tracker and curl receiver initialized at Hip:\n" +
                    hip.name +
                    "\n\nTail sender remains on Tail Tip:\n" +
                    tailTip.name +
                    "\n\nCollision Tag:\n" +
                    collisionTag +
                    "\n\nTracker FX was " +
                    (mergeTrackerFX
                        ? "merged into"
                        : "not merged into") +
                    " the avatar FX controller.",
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

        private void CreateTestObjectPrefab()
        {
            if (string.IsNullOrWhiteSpace(collisionTag))
                throw new Exception("Collision Tag cannot be empty before creating the test prefab.");

            if (testObjectScale <= 0)
                throw new Exception("Test Object Scale must be greater than zero.");

            if (testMoveSpeed <= 0)
                throw new Exception("Test Move Speed must be greater than zero.");

            EnsureFolder("Assets", "TailSeek", "Generated");

            // Keep the root unscaled so the Contact Sender radius is not
            // multiplied by the visual mesh scale.
            GameObject testObject = new GameObject("TailSeek Test Object");

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
            mover.simulateContacts = true;

            string prefabPath = testPrefabPath.Replace("\\", "/");
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null)
                AssetDatabase.DeleteAsset(prefabPath);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(testObject, prefabPath);
            if (prefab == null)
            {
                DestroyImmediate(testObject);
                throw new Exception("Unity could not save the TailSeek Test Object prefab at " + prefabPath);
            }

            DestroyImmediate(testObject);
            Debug.Log("Tail Seek: created test prefab at " + prefabPath);
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
            if (avatar == null)
                throw new Exception(
                    "Avatar is not assigned.");

            if (tailRoot == null)
                throw new Exception(
                    "Tail Root is not assigned.");

            if (tailTip == null)
                throw new Exception(
                    "Tail Tip is not assigned.");

            if (hip == null)
                throw new Exception(
                    "Hip is not assigned.");

            if (contactTrackerPrefab == null)
                throw new Exception(
                    "Contact Tracker Prefab is not assigned.");

            if (contactTrackerController == null)
                throw new Exception(
                    "Contact Tracker FX Controller is not assigned.");

            if (addCurlAnimator &&
                curlAnimation == null)
            {
                throw new Exception(
                    "Curl Animation is not assigned.");
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

                if (fullCurlDistance < 0 ||
                    fullCurlDistance >= curlDistance)
                {
                    throw new Exception(
                        "Full Curl Distance must be greater than or equal to 0 " +
                        "and strictly smaller than Curl Start Distance.");
                }

                if (string.IsNullOrWhiteSpace(
                    curlParameter))
                {
                    throw new Exception(
                        "Curl Parameter cannot be empty.");
                }
            }

            if (aimAxis == Vector3.zero)
            {
                throw new Exception(
                    "Tail Forward Axis cannot be zero.");
            }

            if (upAxis == Vector3.zero)
            {
                throw new Exception(
                    "Tail Up Axis cannot be zero.");
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
            for (int i =
                    controller.layers.Length - 1;
                 i >= 0;
                 i--)
            {
                if (controller.layers[i].name ==
                    CurlLayerName)
                {
                    controller.RemoveLayer(i);
                }
            }
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
        // AIM CONSTRAINT
        // =====================================================================

        private void CreateAimConstraint(
            Transform tail,
            Transform target)
        {
            AimConstraint constraint =
                tail.GetComponent<AimConstraint>();

            if (constraint == null)
            {
                constraint =
                    Undo.AddComponent<AimConstraint>(
                        tail.gameObject);
            }

            constraint.constraintActive =
                false;

            constraint.locked =
                false;

            for (int i =
                    constraint.sourceCount - 1;
                 i >= 0;
                 i--)
            {
                constraint.RemoveSource(i);
            }

            constraint.aimVector =
                aimAxis.normalized;

            constraint.upVector =
                upAxis.normalized;

            constraint.worldUpType =
                AimConstraint
                    .WorldUpType
                    .SceneUp;

            constraint.rotationAtRest =
                tail.localRotation.eulerAngles;

            constraint.rotationOffset =
                Vector3.zero;

            ConstraintSource source =
                new ConstraintSource
                {
                    sourceTransform = target,
                    weight = 1.0f
                };

            constraint.AddSource(
                source);

            constraint.weight =
                1.0f;

            constraint.constraintActive =
                true;

            EditorUtility.SetDirty(
                constraint);
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

        private void AddCurlParameter(
            AnimatorController controller)
        {
            AnimatorControllerParameter existing =
                FindParameter(
                    controller,
                    curlParameter);

            if (existing != null)
            {
                if (existing.type !=
                    AnimatorControllerParameterType.Float)
                {
                    throw new Exception(
                        "Animator parameter '" +
                        curlParameter +
                        "' already exists but is not a Float.");
                }

                return;
            }

            AnimatorControllerParameter parameter =
                new AnimatorControllerParameter
                {
                    name = curlParameter,
                    type =
                        AnimatorControllerParameterType.Float,
                    defaultFloat = 0f
                };

            controller.AddParameter(
                parameter);

            EditorUtility.SetDirty(
                controller);
        }

        private void AddCurlLayer(
            AnimatorController controller)
        {
            RemoveGeneratedCurlLayer(
                controller);

            string controllerPath =
                AssetDatabase.GetAssetPath(
                    controller);

            AnimatorControllerLayer layer =
                new AnimatorControllerLayer
                {
                    name = CurlLayerName,
                    defaultWeight = 1.0f,
                    blendingMode =
                        AnimatorLayerBlendingMode.Override,
                    stateMachine =
                        new AnimatorStateMachine()
                };

            layer.stateMachine.name =
                CurlLayerName;

            layer.stateMachine.hideFlags =
                HideFlags.HideInHierarchy;

            if (!string.IsNullOrEmpty(
                controllerPath))
            {
                AssetDatabase.AddObjectToAsset(
                    layer.stateMachine,
                    controllerPath);
            }

            controller.AddLayer(
                layer);

            PersistLayerDefaultWeight(
                controller,
                CurlLayerName,
                1.0f);

            AnimatorStateMachine machine =
                GetLayerStateMachine(
                    controller,
                    CurlLayerName);

            if (machine == null)
            {
                throw new Exception(
                    "Unity did not keep the Tail Seek curl layer state machine.");
            }

            AnimatorState curlState =
                machine.AddState(
                    "Curl Depth",
                    new Vector3(250, 0, 0));

            curlState.hideFlags =
                HideFlags.HideInHierarchy;

            curlState.motion =
                curlAnimation;

            curlState.writeDefaultValues =
                false;

            curlState.speed =
                1.0f;

            curlState.speedParameterActive =
                false;

            curlState.timeParameterActive =
                true;

            curlState.timeParameter =
                curlParameter;

            if (curlState.motion == null)
            {
                throw new Exception(
                    "The curl animation was not saved onto the Curl Depth state. " +
                    "Make sure the Curl Animation field is assigned.");
            }

            machine.defaultState =
                curlState;

            PersistLayerDefaultWeight(
                controller,
                CurlLayerName,
                1.0f);

            EditorUtility.SetDirty(
                curlState);

            EditorUtility.SetDirty(
                machine);

            EditorUtility.SetDirty(
                controller);
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
