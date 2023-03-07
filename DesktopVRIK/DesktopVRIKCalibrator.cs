﻿using ABI.CCK.Components;
using ABI_RC.Core;
using ABI_RC.Core.Base;
using ABI_RC.Core.Player;
using ABI_RC.Systems.IK;
using ABI_RC.Systems.IK.SubSystems;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.Events;

namespace NAK.Melons.DesktopVRIK;

public class DesktopVRIKCalibrator
{
    // Settings
    public bool Setting_UseVRIKToes = true;
    public bool Setting_FindUnmappedToes = true;
    public bool Setting_ExperimentalKneeBend = true;
    public bool Setting_DebugCalibrationPose = false;

    // Avatar Component References
    public CVRAvatar avatar;
    public Animator animator;
    public Transform avatarTransform;
    public VRIK vrik;
    public LookAtIK lookAtIK;

    // Calibrated Values
    public float 
        initialFootDistance, 
        initialStepThreshold, 
        initialStepHeight;

    // Calibration Internals
    bool DebugCalibrationPose;
    bool fixTransformsRequired;
    Vector3 leftKneeNormal, rightKneeNormal;
    HumanPose initialHumanPose;
    HumanPoseHandler humanPoseHandler;
    // Traverse
    IKSystem ikSystem;
    PlayerSetup playerSetup;
    Traverse
        _vrikTraverse, 
        _lookIKTraverse, 
        _avatarTraverse, 
        _animatorManagerTraverse, 
        _poseHandlerTraverse, 
        _avatarRootHeightTraverse;

    public DesktopVRIKCalibrator()
    {
        // Get base game scripts.
        ikSystem = IKSystem.Instance;
        playerSetup = PlayerSetup.Instance;

        // Get traverse to private shit in iksystem.
        _vrikTraverse = Traverse.Create(ikSystem).Field("_vrik");
        _avatarTraverse = Traverse.Create(ikSystem).Field("_avatar");
        _animatorManagerTraverse = Traverse.Create(ikSystem).Field("_animatorManager");
        _poseHandlerTraverse = Traverse.Create(ikSystem).Field("_poseHandler");
        _avatarRootHeightTraverse = Traverse.Create(ikSystem).Field("_avatarRootHeight");

        // Get traverse to private shit in playersetup.
        _lookIKTraverse = Traverse.Create(playerSetup).Field("lookIK");
    }

    public void CalibrateDesktopVRIK()
    {
        PreInitialize();

        // Don't do anything else if just debugging calibration pose
        DebugCalibrationPose = !DebugCalibrationPose;
        if (Setting_DebugCalibrationPose && DebugCalibrationPose)
        {
            ForceCalibrationPose();
            return;
        }

        // Add VRIK and configure
        PrepareAvatarVRIK();

        Initialize();

        PostInitialize();
    }

    public void ForceCalibrationPose(bool toggle = true)
    {
        animator.enabled = !toggle;
        SetHumanPose(0f);
        //SetAvatarIKPose(toggle);
    }

    private void PreInitialize()
    {
        // Scan avatar for issues/references
        ScanAvatarForCalibration();
        // Prepare CVR IKSystem for external VRIK
        PrepareIKSystem();
    }

    private void Initialize()
    {
        // Calculate bend normals with motorcycle pose
        SetHumanPose(0f);
        CalculateKneeBendNormals();

        SetAvatarIKPose(true);

        // Setup HeadIK target & calculate initial footstep values
        SetupDesktopHeadIKTarget();
        CalculateInitialIKScaling();

        // Initiate VRIK manually
        ForceInitiateVRIKSolver();

        SetAvatarIKPose(false);
    }

    private void PostInitialize()
    {
        ApplyKneeBendNormals();
        ApplyInitialIKScaling();
        vrik.onPreSolverUpdate.AddListener(new UnityAction(DesktopVRIK.Instance.OnPreSolverUpdate));
    }
    
    private void ScanAvatarForCalibration()
    {
        // Reset some stuff to default
        fixTransformsRequired = false;

        // Find required avatar components
        avatar = playerSetup._avatar.GetComponent<CVRAvatar>();
        animator = avatar.GetComponent<Animator>();
        avatarTransform = avatar.transform;
        lookAtIK = _lookIKTraverse.GetValue<LookAtIK>();

        // Apply some fixes for weird setups
        if (!animator.enabled)
        {
            fixTransformsRequired = true;
            DesktopVRIKMod.Logger.Error("Avatar has Animator disabled by default!");
        }

        // Center avatar local offsets
        avatarTransform.localPosition = Vector3.zero;
        //avatarTransform.localRotation = Quaternion.identity;

        // Store original human pose
        if (humanPoseHandler != null)
        {
            humanPoseHandler.Dispose();
        }
        humanPoseHandler = new HumanPoseHandler(animator.avatar, avatarTransform);
        humanPoseHandler.GetHumanPose(ref initialHumanPose);
    }

    private void PrepareIKSystem()
    {
        // Get the animator manager and human pose handler
        var animatorManager = _animatorManagerTraverse.GetValue<CVRAnimatorManager>();
        var ikHumanPoseHandler = _poseHandlerTraverse.GetValue<HumanPoseHandler>();

        // Store the avatar component
        _avatarTraverse.SetValue(avatar);

        // Set the animator for the IK system
        ikSystem.animator = animator;
        if (ikSystem.animator != null)
        {
            animatorManager.SetAnimator(ikSystem.animator, ikSystem.animator.runtimeAnimatorController);
        }

        // Set the avatar height float
        float avatarHeight = ikSystem.vrPlaySpace.transform.InverseTransformPoint(avatarTransform.position).y;
        _avatarRootHeightTraverse.SetValue(avatarHeight);

        // Create a new human pose handler and dispose the old one
        if (ikHumanPoseHandler != null)
        {
            ikHumanPoseHandler.Dispose();
            _poseHandlerTraverse.SetValue(null);
        }
        ikHumanPoseHandler = new HumanPoseHandler(ikSystem.animator.avatar, avatarTransform);
        _poseHandlerTraverse.SetValue(ikHumanPoseHandler);

        // Find valid human bones
        IKSystem.BoneExists.Clear();
        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (bone != HumanBodyBones.LastBone)
            {
                IKSystem.BoneExists.Add(bone, ikSystem.animator.GetBoneTransform(bone) != null);
            }
        }

        // Prepare BodySystem for calibration
        BodySystem.TrackingLeftArmEnabled = false;
        BodySystem.TrackingRightArmEnabled = false;
        BodySystem.TrackingLeftLegEnabled = false;
        BodySystem.TrackingRightLegEnabled = false;
        BodySystem.TrackingPositionWeight = 0f;
    }

    private void PrepareAvatarVRIK()
    {
        //add and configure VRIK
        vrik = avatar.gameObject.AddComponentIfMissing<VRIK>();
        vrik.AutoDetectReferences();
        ConfigureVRIKReferences();
        _vrikTraverse.SetValue(vrik);

        //in testing, not really needed
        //only required if Setting_FindUnmappedToes
        //and non-human mapped toes are found
        vrik.fixTransforms = fixTransformsRequired;

        //default solver settings
        vrik.solver.locomotion.weight = 0f;
        vrik.solver.locomotion.angleThreshold = 30f;
        vrik.solver.locomotion.maxLegStretch = 0.75f;
        vrik.solver.spine.minHeadHeight = 0f;
        vrik.solver.IKPositionWeight = 1f;
        //disable to not bleed into anims
        vrik.solver.spine.chestClampWeight = 0f;
        vrik.solver.spine.maintainPelvisPosition = 0f;
        //for body leaning
        vrik.solver.spine.neckStiffness = 0.0001f; //cannot be 0
        vrik.solver.spine.bodyPosStiffness = 1f;
        vrik.solver.spine.bodyRotStiffness = 0.2f;
        //disable so avatar doesnt try and walk away
        vrik.solver.locomotion.velocityFactor = 0f;
        vrik.solver.locomotion.maxVelocity = 0f;
        //fixes nameplate spazzing on remote & magicacloth
        vrik.solver.locomotion.rootSpeed = 1000f;
        //disable so PAM & BID dont make body shake
        vrik.solver.spine.rotateChestByHands = 0f;
        //enable to prioritize LookAtIK
        vrik.solver.spine.headClampWeight = 0.2f;
        //disable to not go on tippytoes
        vrik.solver.spine.positionWeight = 0f;
        vrik.solver.spine.rotationWeight = 1f;

        //vrik.solver.spine.maintainPelvisPosition = 1f;
        //vrik.solver.locomotion.weight = 0f;
        //vrik.solver.spine.positionWeight = 0f;
        //vrik.solver.spine.pelvisPositionWeight = 0f;
        //vrik.solver.leftArm.positionWeight = 0f;
        //vrik.solver.leftArm.rotationWeight = 0f;
        //vrik.solver.rightArm.positionWeight = 0f;
        //vrik.solver.rightArm.rotationWeight = 0f;
        //vrik.solver.leftLeg.positionWeight = 0f;
        //vrik.solver.leftLeg.rotationWeight = 0f;
        //vrik.solver.rightLeg.positionWeight = 0f;
        //vrik.solver.rightLeg.rotationWeight = 0f;
        //vrik.solver.IKPositionWeight = 0f;

        //THESE ARE CONFIGURABLE IN GAME IK SETTINGS
        //vrik.solver.leftLeg.target = null;
        //vrik.solver.leftLeg.bendGoal = null;
        //vrik.solver.leftLeg.positionWeight = 0f;
        //vrik.solver.leftLeg.bendGoalWeight = 0f;
        //vrik.solver.rightLeg.target = null;
        //vrik.solver.rightLeg.bendGoal = null;
        //vrik.solver.rightLeg.positionWeight = 0f;
        //vrik.solver.rightLeg.bendGoalWeight = 0f;
        //vrik.solver.spine.pelvisTarget = null;
        //vrik.solver.spine.chestGoal = null;
        //vrik.solver.spine.positionWeight = 0f;
        //vrik.solver.spine.rotationWeight = 0f;
        //vrik.solver.spine.pelvisPositionWeight = 0f;
        //vrik.solver.spine.pelvisRotationWeight = 0f;
        //vrik.solver.spine.chestGoalWeight = 0f;
    }

    private void CalculateKneeBendNormals()
    {
        // Get assumed left knee normal
        Vector3[] leftVectors = new Vector3[]
         {
                vrik.references.leftThigh?.position ?? Vector3.zero,
                vrik.references.leftCalf?.position ?? Vector3.zero,
                vrik.references.leftFoot?.position ?? Vector3.zero,
         };
        leftKneeNormal = Quaternion.Inverse(vrik.references.root.rotation) * GetNormalFromArray(leftVectors);

        // Get assumed right knee normal
        Vector3[] rightVectors = new Vector3[]
         {
                vrik.references.rightThigh?.position ?? Vector3.zero,
                vrik.references.rightCalf?.position ?? Vector3.zero,
                vrik.references.rightFoot?.position ?? Vector3.zero,
         };
        rightKneeNormal = Quaternion.Inverse(vrik.references.root.rotation) * GetNormalFromArray(rightVectors);
    }
    
    private void ApplyKneeBendNormals()
    {
        if (!Setting_ExperimentalKneeBend)
        {
            //enable so knees on fucked models work better
            vrik.solver.leftLeg.useAnimatedBendNormal = true;
            vrik.solver.rightLeg.useAnimatedBendNormal = true;
            return;
        }

        vrik.solver.leftLeg.bendToTargetWeight = 0f;
        vrik.solver.rightLeg.bendToTargetWeight = 0f;

        Traverse leftLeg_bendNormalRelToPelvisTraverse = Traverse.Create(vrik.solver.leftLeg).Field("bendNormalRelToPelvis");
        Traverse rightLeg_bendNormalRelToPelvisTraverse = Traverse.Create(vrik.solver.rightLeg).Field("bendNormalRelToPelvis");

        // Calculate knee normal without root rotation but with pelvis rotation
        Quaternion pelvisLocalRotationInverse = Quaternion.Inverse(vrik.references.pelvis.localRotation);
        Vector3 leftLegBendNormalRelToPelvis = pelvisLocalRotationInverse * leftKneeNormal;
        Vector3 rightLegBendNormalRelToPelvis = pelvisLocalRotationInverse * rightKneeNormal;
        //Quaternion rootRotation = vrik.references.root.rotation;
        //Quaternion pelvisRotationRelativeToRoot = Quaternion.Inverse(rootRotation) * vrik.references.pelvis.rotation;
        //Quaternion pelvisRotationInverse = Quaternion.Inverse(pelvisRotationRelativeToRoot);
        leftLeg_bendNormalRelToPelvisTraverse.SetValue(leftLegBendNormalRelToPelvis);
        rightLeg_bendNormalRelToPelvisTraverse.SetValue(rightLegBendNormalRelToPelvis);
    }

    private Vector3 GetNormalFromArray(Vector3[] positions)
    {
        Vector3 vector = Vector3.zero;
        Vector3 vector2 = Vector3.zero;
        for (int i = 0; i < positions.Length; i++)
        {
            vector2 += positions[i];
        }
        vector2 /= (float)positions.Length;
        for (int j = 0; j < positions.Length - 1; j++)
        {
            vector += Vector3.Cross(positions[j] - vector2, positions[j + 1] - vector2).normalized;
        }
        return Vector3.Normalize(vector);
    }

    private void CalculateInitialIKScaling()
    {
        // Get distance between feets and thighs
        float footDistance = Vector3.Distance(vrik.references.leftFoot.position, vrik.references.rightFoot.position);
        initialFootDistance = footDistance * 0.5f;
        initialStepThreshold = footDistance * 0.8f;
        initialStepHeight = Vector3.Distance(vrik.references.leftFoot.position, vrik.references.leftCalf.position) * 0.2f;
    }

    private void ApplyInitialIKScaling()
    {
        // Set initial values
        vrik.solver.locomotion.footDistance = initialFootDistance;
        vrik.solver.locomotion.stepThreshold = initialStepThreshold;
        DesktopVRIK.ScaleStepHeight(vrik.solver.locomotion.stepHeight, initialStepHeight);
    }
    
    private void SetupDesktopHeadIKTarget()
    {
        // Lazy HeadIKTarget calibration
        if (vrik.solver.spine.headTarget == null)
        {
            vrik.solver.spine.headTarget = new GameObject("Head IK Target").transform;
        }
        vrik.solver.spine.headTarget.parent = vrik.references.head;
        vrik.solver.spine.headTarget.localPosition = Vector3.zero;
        vrik.solver.spine.headTarget.localRotation = Quaternion.identity;
    }

    private void SetAvatarIKPose(bool enforceTPose)
    {
        int ikposeLayerIndex = animator.GetLayerIndex("IKPose");
        int locoLayerIndex = animator.GetLayerIndex("Locomotion/Emotes");

        // Use custom IKPose if found.
        if (ikposeLayerIndex != -1 && locoLayerIndex != -1)
        {
            animator.SetLayerWeight(ikposeLayerIndex, enforceTPose ? 1f : 0f);
            animator.SetLayerWeight(locoLayerIndex, enforceTPose ? 0f : 1f);
            animator.Update(0f);
            return;
        }

        // Otherwise use DesktopVRIK IKPose & revert afterwards.
        if (enforceTPose)
        {
            SetHumanPose(1f);
        }
        else
        {
            humanPoseHandler.SetHumanPose(ref initialHumanPose);
        }
    }

    private void SetHumanPose(float ikPoseWeight = 1f)
    {
        humanPoseHandler.GetHumanPose(ref ikSystem.humanPose);
        for (int i = 0; i < ikSystem.humanPose.muscles.Length; i++)
        {
            float weight = ikPoseWeight * IKPoseMuscles[i];
            IKSystem.Instance.ApplyMuscleValue((MuscleIndex)i, weight, ref ikSystem.humanPose.muscles);
        }
        ikSystem.humanPose.bodyRotation = Quaternion.identity;
        humanPoseHandler.SetHumanPose(ref ikSystem.humanPose);
    }

    private void ForceInitiateVRIKSolver()
    {
        //force immediate calibration before animator decides to fuck us
        vrik.solver.SetToReferences(vrik.references);
        vrik.solver.Initiate(vrik.transform);
    }

    private void ConfigureVRIKReferences()
    {
        //might not work over netik
        FixChestAndSpineReferences();

        if (!Setting_UseVRIKToes)
        {
            vrik.references.leftToes = null;
            vrik.references.rightToes = null;
        }
        else if (Setting_FindUnmappedToes)
        {
            //doesnt work with netik, but its toes...
            FindAndSetUnmappedToes();
        }

        //bullshit fix to not cause death
        FixFingerBonesError();
    }

    private void FixChestAndSpineReferences()
    {
        Transform leftShoulderBone = vrik.references.leftShoulder;
        Transform rightShoulderBone = vrik.references.rightShoulder;
        Transform assumedChest = leftShoulderBone?.parent;

        if (assumedChest != null && rightShoulderBone.parent == assumedChest &&
            vrik.references.chest != assumedChest)
        {
            vrik.references.chest = assumedChest;
            vrik.references.spine = assumedChest.parent;
        }
    }

    private void FindAndSetUnmappedToes()
    {
        Transform leftToes = vrik.references.leftToes;
        Transform rightToes = vrik.references.rightToes;

        if (leftToes == null && rightToes == null)
        {
            leftToes = FindUnmappedToe(vrik.references.leftFoot);
            rightToes = FindUnmappedToe(vrik.references.rightFoot);

            if (leftToes != null && rightToes != null)
            {
                vrik.references.leftToes = leftToes;
                vrik.references.rightToes = rightToes;
                fixTransformsRequired = true;
            }
        }
    }

    private Transform FindUnmappedToe(Transform foot)
    {
        foreach (Transform bone in foot)
        {
            if (bone.name.ToLowerInvariant().Contains("toe") ||
                bone.name.ToLowerInvariant().EndsWith("_end"))
            {
                return bone;
            }
        }

        return null;
    }

    private void FixFingerBonesError()
    {
        FixFingerBones(vrik.references.leftHand, vrik.solver.leftArm);
        FixFingerBones(vrik.references.rightHand, vrik.solver.rightArm);
    }

    private void FixFingerBones(Transform hand, IKSolverVR.Arm armSolver)
    {
        if (hand.childCount == 0)
        {
            armSolver.wristToPalmAxis = Vector3.up;
            armSolver.palmToThumbAxis = hand == vrik.references.leftHand ? -Vector3.forward : Vector3.forward;
        }
    }

    private static readonly float[] IKPoseMuscles = new float[]
     {
            0.00133321f,
            8.195831E-06f,
            8.537738E-07f,
            -0.002669832f,
            -7.651234E-06f,
            -0.001659694f,
            0f,
            0f,
            0f,
            0.04213953f,
            0.0003007996f,
            -0.008032114f,
            -0.03059979f,
            -0.0003182998f,
            0.009640567f,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            0.5768794f,
            0.01061097f,
            -0.1127839f,
            0.9705755f,
            0.07972051f,
            -0.0268422f,
            0.007237188f,
            0f,
            0.5768792f,
            0.01056608f,
            -0.1127519f,
            0.9705756f,
            0.07971933f,
            -0.02682396f,
            0.007229362f,
            0f,
            -5.651802E-06f,
            -3.034899E-07f,
            0.4100508f,
            0.3610304f,
            -0.0838329f,
            0.9262537f,
            0.1353517f,
            -0.03578902f,
            0.06005657f,
            -4.95989E-06f,
            -1.43007E-06f,
            0.4096187f,
            0.363263f,
            -0.08205152f,
            0.9250782f,
            0.1345718f,
            -0.03572125f,
            0.06055461f,
            -1.079177f,
            0.2095419f,
            0.6140652f,
            0.6365265f,
            0.6683931f,
            -0.4764312f,
            0.8099416f,
            0.8099371f,
            0.6658203f,
            -0.7327053f,
            0.8113618f,
            0.8114051f,
            0.6643661f,
            -0.40341f,
            0.8111364f,
            0.8111367f,
            0.6170399f,
            -0.2524227f,
            0.8138723f,
            0.8110135f,
            -1.079171f,
            0.2095456f,
            0.6140658f,
            0.6365255f,
            0.6683878f,
            -0.4764301f,
            0.8099402f,
            0.8099376f,
            0.6658241f,
            -0.7327023f,
            0.8113653f,
            0.8113793f,
            0.664364f,
            -0.4034042f,
            0.811136f,
            0.8111364f,
            0.6170469f,
            -0.2524345f,
            0.8138595f,
            0.8110138f
     };
}