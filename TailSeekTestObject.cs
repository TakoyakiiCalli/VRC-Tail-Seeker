using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;

/// <summary>
/// Editor test sender for Tail Seek.
///
/// A scene VRCContactSender is not another VRChat player, so avatar receivers
/// with Allow Self disabled ignore it. Gesture Manager also owns the FX
/// playable graph: Animator.SetFloat does not move those parameters, and the
/// VRChat Contact Manager writes 0 over any value we set on paramAccess.
///
/// This component wraps each matching receiver's paramAccess so Contact Manager
/// zeros cannot stick, and also writes Gesture Manager playable params by
/// reflection.
/// </summary>
[DefaultExecutionOrder(32000)]
public class TailSeekTestObject : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 1.5f;
    public float fastMultiplier = 3.0f;

    [Header("Contact Simulation")]
    [Tooltip("Drive matching avatar Contact Receivers in the editor / Gesture Manager.")]
    public bool simulateContacts = true;

    [Tooltip("Optional override. If empty, the VRCContactSender collision tags are used.")]
    public string collisionTag = "";

    [Tooltip("Radius at which curl Motion Time reaches 1. Must be no larger than the hip receiver radius.")]
    public float fullCurlDistance = 2.0f;

    [Header("Debug")]
    public float debugCurl;
    public float debugTrackerMax;
    public float debugLeft;
    public float debugRight;
    public int debugMatchedReceivers;
    public int debugParamAccessCount;
    public bool debugGestureManager;

    private VRCContactSender sender;
    private VRCContactReceiver[] receiverCache;
    private float receiverCacheTime = -1f;
    private bool loggedFirstHit;
    private bool loggedMissingGestureManager;
    private readonly List<SimulatedParamAccess> overlays = new List<SimulatedParamAccess>();

    private static Type gestureManagerType;
    private static FieldInfo controlledAvatarsField;
    private static MethodInfo getParamMethod;
    private static MethodInfo paramSetMethod;
    private static MethodInfo paramInternalSetMethod;
    private static bool gestureManagerLookupDone;

    private void Awake()
    {
        sender = GetComponent<VRCContactSender>();
        if (sender == null)
            sender = GetComponentInChildren<VRCContactSender>();
    }

    private void OnDisable()
    {
        RestoreOverlays();
    }

    private void OnDestroy()
    {
        RestoreOverlays();
    }

    private void Update()
    {
        if (Application.isPlaying)
            HandleMovement();

        if (simulateContacts)
            SimulateContacts();
    }

    private void LateUpdate()
    {
        if (simulateContacts)
            SimulateContacts();
    }

    private void HandleMovement()
    {
        Vector3 input = Vector3.zero;

        if (Input.GetKey(KeyCode.A))
            input.x -= 1f;

        if (Input.GetKey(KeyCode.D))
            input.x += 1f;

        if (Input.GetKey(KeyCode.Q))
            input.y -= 1f;

        if (Input.GetKey(KeyCode.E))
            input.y += 1f;

        if (Input.GetKey(KeyCode.S))
            input.z -= 1f;

        if (Input.GetKey(KeyCode.W))
            input.z += 1f;

        if (input.sqrMagnitude > 1f)
            input.Normalize();

        float speed = moveSpeed;

        if (Input.GetKey(KeyCode.LeftShift) ||
            Input.GetKey(KeyCode.RightShift))
        {
            speed *= fastMultiplier;
        }

        transform.position += input * speed * Time.deltaTime;
    }

    private void SimulateContacts()
    {
        debugCurl = 0f;
        debugTrackerMax = 0f;
        debugLeft = 0f;
        debugRight = 0f;
        debugMatchedReceivers = 0;
        debugParamAccessCount = 0;
        debugGestureManager = GestureManagerIsControlling();

        if (!debugGestureManager)
            WarnMissingGestureManager();

        RefreshReceiverCache();
        if (receiverCache != null)
        {
            Vector3 senderCenter = GetContactWorldPosition(sender, transform);
            float senderRadius = GetContactWorldRadius(sender, transform, 0.025f);

            for (int i = 0; i < receiverCache.Length; i++)
            {
                VRCContactReceiver receiver = receiverCache[i];
                if (receiver == null || !receiver.isActiveAndEnabled)
                    continue;

                if (receiver.transform.IsChildOf(transform))
                    continue;

                if (!TagsMatch(receiver))
                    continue;

                if (string.IsNullOrEmpty(receiver.parameter))
                    continue;

                float proximity = ComputeProximity(
                    senderCenter,
                    senderRadius,
                    receiver);

                debugMatchedReceivers++;
                WriteReceiver(receiver, proximity);
                TrackDebug(receiver.parameter, proximity);
            }
        }

        DriveFromAvatarHips();

        if (debugTrackerMax > 0.01f || debugCurl > 0.01f)
            TryWriteGestureManager("ContactTracker/Control", 1f);

        if (!loggedFirstHit && debugCurl > 0.01f)
        {
            loggedFirstHit = true;
            Debug.Log(
                "Tail Seek: curl proximity " + debugCurl.ToString("0.00") +
                ", left " + debugLeft.ToString("0.00") +
                ", right " + debugRight.ToString("0.00") +
                ", matched " + debugMatchedReceivers +
                ", paramAccess " + debugParamAccessCount +
                ", Gesture Manager " + (debugGestureManager ? "controlling" : "NOT controlling") +
                ".");
        }
    }

    private void WriteReceiver(VRCContactReceiver receiver, float proximity)
    {
        SimulatedParamAccess overlay = EnsureOverlay(receiver);
        overlay.Simulated = proximity;
        overlay.Hold = true;
        overlay.Push();

        WriteFloat(receiver.parameter, proximity, GetAvatarAnimator(receiver));

        if (receiver.parameter.IndexOf("Curl", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            float receiverRadius = GetContactWorldRadius(
                receiver,
                receiver.transform,
                2.0f);

            float curlTime = RemapToFullCurl(
                proximity,
                receiverRadius,
                fullCurlDistance);

            WriteFloat(receiver.parameter + "_Time", curlTime, GetAvatarAnimator(receiver));
        }
    }

    private void TrackDebug(string parameter, float proximity)
    {
        if (string.IsNullOrEmpty(parameter))
            return;

        if (parameter.IndexOf("Curl", StringComparison.OrdinalIgnoreCase) >= 0 &&
            parameter.IndexOf("Time", StringComparison.OrdinalIgnoreCase) < 0)
        {
            if (proximity > debugCurl)
                debugCurl = proximity;
        }

        if (parameter == "ContactTracker/X-")
            debugLeft = Mathf.Max(debugLeft, proximity);

        if (parameter == "ContactTracker/X+")
            debugRight = Mathf.Max(debugRight, proximity);

        if (parameter.StartsWith("ContactTracker/", StringComparison.Ordinal) &&
            proximity > debugTrackerMax)
        {
            debugTrackerMax = proximity;
        }
    }

    private void DriveFromAvatarHips()
    {
        VRCAvatarDescriptor[] avatars = FindObjectsOfType<VRCAvatarDescriptor>();
        if (avatars == null || avatars.Length == 0)
            return;

        for (int i = 0; i < avatars.Length; i++)
        {
            VRCAvatarDescriptor avatar = avatars[i];
            if (avatar == null || !avatar.isActiveAndEnabled)
                continue;

            Transform hip = FindHipTransform(avatar.transform);
            if (hip == null)
                continue;

            Transform curlReceiverTransform = FindNamedChild(
                avatar.transform,
                "TailSeek Curl Receiver");

            Transform probe = curlReceiverTransform != null
                ? curlReceiverTransform
                : hip;

            VRCContactReceiver curlReceiver =
                curlReceiverTransform != null
                    ? curlReceiverTransform.GetComponent<VRCContactReceiver>()
                    : null;

            float curlRadius = GetContactWorldRadius(curlReceiver, probe, 2.0f);
            Vector3 probeCenter = curlReceiver != null
                ? GetContactWorldPosition(curlReceiver, probe)
                : probe.position;

            float senderRadius = GetContactWorldRadius(sender, transform, 0.025f);
            float centerDistance = Vector3.Distance(transform.position, probeCenter);
            float closest = Mathf.Max(0f, centerDistance - senderRadius);
            float proximity = closest >= curlRadius
                ? 0f
                : Mathf.Clamp01(1f - closest / curlRadius);

            float curlTime = RemapToFullCurl(
                proximity,
                curlRadius,
                fullCurlDistance);

            Animator animator = avatar.GetComponent<Animator>();
            string curlName = curlReceiver != null &&
                !string.IsNullOrEmpty(curlReceiver.parameter)
                    ? curlReceiver.parameter
                    : "TailSeek_Curl";

            WriteFloat(curlName, proximity, animator);
            WriteFloat(curlName + "_Time", curlTime, animator);

            Vector3 local = hip.InverseTransformPoint(transform.position);
            float axisRange = Mathf.Max(curlRadius, 0.0001f);
            float right = Mathf.Clamp01(local.x / axisRange);
            float left = Mathf.Clamp01(-local.x / axisRange);
            float up = Mathf.Clamp01(local.y / axisRange);
            float down = Mathf.Clamp01(-local.y / axisRange);
            float forward = Mathf.Clamp01(local.z / axisRange);
            float back = Mathf.Clamp01(-local.z / axisRange);

            WriteFloat("ContactTracker/X+", right, animator);
            WriteFloat("ContactTracker/X-", left, animator);
            WriteFloat("ContactTracker/Y+", up, animator);
            WriteFloat("ContactTracker/Y-", down, animator);
            WriteFloat("ContactTracker/Z+", forward, animator);
            WriteFloat("ContactTracker/Z-", back, animator);

            if (proximity > 0.01f)
                WriteFloat("ContactTracker/Control", 1f, animator);

            if (curlTime > debugCurl)
                debugCurl = curlTime;

            debugLeft = Mathf.Max(debugLeft, left);
            debugRight = Mathf.Max(debugRight, right);
            debugTrackerMax = Mathf.Max(
                debugTrackerMax,
                Mathf.Max(left, right));
        }
    }

    private static Transform FindHipTransform(Transform avatarRoot)
    {
        Transform curl = FindNamedChild(avatarRoot, "TailSeek Curl Receiver");
        if (curl != null && curl.parent != null)
            return curl.parent;

        Transform armature = FindNamedChild(avatarRoot, "Armature");
        if (armature != null && armature.childCount > 0)
            return armature.GetChild(0);

        Transform hips = FindNamedChild(avatarRoot, "Hips");
        if (hips != null)
            return hips;

        return avatarRoot;
    }

    private static Transform FindNamedChild(Transform parent, string name)
    {
        if (parent == null)
            return null;

        if (parent.name == name)
            return parent;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform found = FindNamedChild(parent.GetChild(i), name);
            if (found != null)
                return found;
        }

        return null;
    }

    private void WriteFloat(string parameterName, float value, Animator animator)
    {
        TryWriteGestureManager(parameterName, value);

        if (animator != null &&
            animator.isInitialized &&
            HasFloatParameter(animator, parameterName))
        {
            animator.SetFloat(parameterName, value);
        }
    }

    private void WarnMissingGestureManager()
    {
        if (loggedMissingGestureManager || !Application.isPlaying)
            return;

        loggedMissingGestureManager = true;
        Debug.LogWarning(
            "Tail Seek: Gesture Manager is not controlling an avatar. " +
            "Enter Play Mode, then select Gesture Manager in the Hierarchy. " +
            "Without that, FX parameters will stay at 0 and the tail will not curl.");
    }

    private bool GestureManagerIsControlling()
    {
        CacheGestureManagerReflection();
        if (controlledAvatarsField == null)
            return false;

        object controlled = controlledAvatarsField.GetValue(null);
        return controlled is System.Collections.IDictionary avatars &&
               avatars.Count > 0;
    }

    private SimulatedParamAccess EnsureOverlay(VRCContactReceiver receiver)
    {
        SimulatedParamAccess existing = receiver.paramAccess as SimulatedParamAccess;
        if (existing != null)
        {
            debugParamAccessCount++;
            return existing;
        }

        SimulatedParamAccess overlay = new SimulatedParamAccess
        {
            Receiver = receiver,
            Inner = receiver.paramAccess,
            ParameterName = receiver.parameter
        };

        receiver.paramAccess = overlay;
        overlays.Add(overlay);

        if (overlay.Inner != null)
            debugParamAccessCount++;

        AlignSenderAsOtherPlayer(receiver);
        return overlay;
    }

    private void AlignSenderAsOtherPlayer(VRCContactReceiver receiver)
    {
        if (sender == null)
            return;

        int receiverId = receiver.playerId;
        sender.playerId = receiverId == 0 ? 1 : receiverId + 1;
        sender.contentTypes = receiver.contentTypes;
    }

    private void RestoreOverlays()
    {
        for (int i = 0; i < overlays.Count; i++)
        {
            SimulatedParamAccess overlay = overlays[i];
            if (overlay == null || overlay.Receiver == null)
                continue;

            if (ReferenceEquals(overlay.Receiver.paramAccess, overlay))
                overlay.Receiver.paramAccess = overlay.Inner;
        }

        overlays.Clear();
    }

    private static Animator GetAvatarAnimator(VRCContactReceiver receiver)
    {
        VRCAvatarDescriptor descriptor =
            receiver.GetComponentInParent<VRCAvatarDescriptor>();

        if (descriptor != null)
        {
            Animator descriptorAnimator = descriptor.GetComponent<Animator>();
            if (descriptorAnimator != null)
                return descriptorAnimator;
        }

        return receiver.GetComponentInParent<Animator>();
    }

    private static bool HasFloatParameter(Animator animator, string parameter)
    {
        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].name == parameter &&
                parameters[i].type == AnimatorControllerParameterType.Float)
            {
                return true;
            }
        }

        return false;
    }

    private bool TagsMatch(VRCContactReceiver receiver)
    {
        if (receiver.collisionTags == null || receiver.collisionTags.Count == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(collisionTag))
            return receiver.collisionTags.Contains(collisionTag);

        if (sender != null &&
            sender.collisionTags != null &&
            sender.collisionTags.Count > 0)
        {
            for (int i = 0; i < sender.collisionTags.Count; i++)
            {
                string tag = sender.collisionTags[i];
                if (!string.IsNullOrEmpty(tag) &&
                    receiver.collisionTags.Contains(tag))
                {
                    return true;
                }
            }

            return false;
        }

        return receiver.collisionTags.Contains("TailSeek");
    }

    private static float RemapToFullCurl(
        float proximity,
        float receiverRadius,
        float fullRadius)
    {
        if (receiverRadius <= 0f)
            return proximity;

        if (fullRadius >= receiverRadius)
            return proximity > 0.0001f ? 1f : 0f;

        float threshold = 1f - fullRadius / receiverRadius;
        if (threshold <= 0.0001f)
            return proximity > 0.0001f ? 1f : 0f;

        return Mathf.Clamp01(proximity / threshold);
    }

    private static float ComputeProximity(
        Vector3 senderCenter,
        float senderRadius,
        VRCContactReceiver receiver)
    {
        Vector3 receiverCenter = GetContactWorldPosition(receiver, receiver.transform);
        float receiverRadius = GetContactWorldRadius(receiver, receiver.transform, 0.5f);
        if (receiverRadius <= 0f)
            return 0f;

        float centerDistance = Vector3.Distance(senderCenter, receiverCenter);
        float closestPointDistance = Mathf.Max(0f, centerDistance - senderRadius);
        if (closestPointDistance >= receiverRadius)
            return 0f;

        return Mathf.Clamp01(1f - closestPointDistance / receiverRadius);
    }

    private static Vector3 GetContactWorldPosition(
        VRC.Dynamics.ContactBase contact,
        Transform fallback)
    {
        if (contact == null)
            return fallback.position;

        Transform root =
            contact.rootTransform != null
                ? contact.rootTransform
                : contact.transform;

        return root.TransformPoint(contact.position);
    }

    private static float GetContactWorldRadius(
        VRC.Dynamics.ContactBase contact,
        Transform fallback,
        float defaultRadius)
    {
        Transform root = fallback;
        float radius = defaultRadius;

        if (contact != null)
        {
            root = contact.rootTransform != null
                ? contact.rootTransform
                : contact.transform;
            radius = contact.radius;
        }

        Vector3 scale = root.lossyScale;
        float uniform =
            (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f;

        return radius * Mathf.Max(uniform, 0.0001f);
    }

    private void RefreshReceiverCache()
    {
        if (receiverCache != null &&
            Time.unscaledTime - receiverCacheTime < 0.5f)
        {
            return;
        }

        receiverCache = FindObjectsOfType<VRCContactReceiver>(true);
        receiverCacheTime = Time.unscaledTime;
    }

    private static void TryWriteGestureManager(string parameterName, float value)
    {
        if (string.IsNullOrEmpty(parameterName))
            return;

        CacheGestureManagerReflection();
        if (controlledAvatarsField == null)
            return;

        object controlled = controlledAvatarsField.GetValue(null);
        if (!(controlled is System.Collections.IDictionary avatars) ||
            avatars.Count == 0)
        {
            return;
        }

        foreach (System.Collections.DictionaryEntry entry in avatars)
        {
            object module = entry.Value;
            if (module == null)
                continue;

            MethodInfo getParam = getParamMethod ?? module.GetType().GetMethod(
                "GetParam",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);

            if (getParam == null)
                continue;

            object param = getParam.Invoke(
                module,
                new object[] { parameterName });

            if (param == null)
                continue;

            MethodInfo setMethod = paramSetMethod;
            if (setMethod == null)
            {
                MethodInfo[] methods = param.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance);

                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != "Set")
                        continue;

                    ParameterInfo[] candidate = methods[i].GetParameters();
                    if (candidate.Length >= 2 &&
                        candidate[1].ParameterType == typeof(float))
                    {
                        setMethod = methods[i];
                        paramSetMethod = setMethod;
                        break;
                    }
                }
            }

            if (setMethod != null)
            {
                ParameterInfo[] args = setMethod.GetParameters();
                if (args.Length >= 3)
                    setMethod.Invoke(param, new object[] { module, value, null });
                else
                    setMethod.Invoke(param, new object[] { module, value });
            }
            else
            {
                MethodInfo internalSet = paramInternalSetMethod ?? param.GetType().GetMethod(
                    "InternalSet",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (internalSet != null)
                {
                    ParameterInfo[] args = internalSet.GetParameters();
                    if (args.Length >= 2)
                        internalSet.Invoke(param, new object[] { value, null });
                    else
                        internalSet.Invoke(param, new object[] { value });
                }
            }
        }
    }

    private static void CacheGestureManagerReflection()
    {
        if (gestureManagerLookupDone)
            return;

        gestureManagerLookupDone = true;

        gestureManagerType = FindType("BlackStartX.GestureManager.GestureManager");
        if (gestureManagerType == null)
            return;

        controlledAvatarsField = gestureManagerType.GetField(
            "ControlledAvatars",
            BindingFlags.Public | BindingFlags.Static);

        Type moduleType = FindType("BlackStartX.GestureManager.Editor.Modules.Vrc3.ModuleVrc3");
        if (moduleType != null)
        {
            getParamMethod = moduleType.GetMethod(
                "GetParam",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
        }

        Type paramType = FindType("BlackStartX.GestureManager.Editor.Modules.Vrc3.Params.Vrc3Param");
        if (paramType == null)
            return;

        MethodInfo[] methods = paramType.GetMethods(
            BindingFlags.Public | BindingFlags.Instance);

        for (int i = 0; i < methods.Length; i++)
        {
            if (methods[i].Name != "Set")
                continue;

            ParameterInfo[] args = methods[i].GetParameters();
            if (args.Length >= 2 &&
                args[1].ParameterType == typeof(float))
            {
                paramSetMethod = methods[i];
                break;
            }
        }

        paramInternalSetMethod = paramType.GetMethod(
            "InternalSet",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static Type FindType(string fullName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Type type = assemblies[i].GetType(fullName);
            if (type != null)
                return type;
        }

        return null;
    }

    private void OnDrawGizmos()
    {
        float radius = GetContactWorldRadius(sender, transform, 0.025f);
        Gizmos.color = debugCurl > 0f
            ? new Color(0.2f, 1f, 0.4f, 0.35f)
            : new Color(1f, 0.85f, 0.2f, 0.25f);
        Gizmos.DrawSphere(transform.position, radius);
        Gizmos.color = debugCurl > 0f
            ? new Color(0.2f, 1f, 0.4f, 1f)
            : new Color(1f, 0.85f, 0.2f, 1f);
        Gizmos.DrawWireSphere(transform.position, radius);
    }

    private sealed class SimulatedParamAccess : IAnimParameterAccess
    {
        public VRCContactReceiver Receiver;
        public IAnimParameterAccess Inner;
        public string ParameterName;
        public float Simulated;
        public bool Hold;

        public void Push()
        {
            if (Inner != null)
                Inner.floatVal = Simulated;

            TryWriteGestureManager(ParameterName, Simulated);
        }

        public bool boolVal
        {
            get => floatVal >= 0.5f;
            set => floatVal = value ? 1f : 0f;
        }

        public int intVal
        {
            get => Mathf.RoundToInt(floatVal);
            set => floatVal = value;
        }

        public float floatVal
        {
            get
            {
                if (Hold)
                    return Simulated;

                return Inner != null ? Inner.floatVal : Simulated;
            }
            set
            {
                float applied = Hold ? Simulated : value;
                Simulated = applied;

                if (Inner != null)
                    Inner.floatVal = applied;

                TryWriteGestureManager(ParameterName, applied);
            }
        }
    }
}
