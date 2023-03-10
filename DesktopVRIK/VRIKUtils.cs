using RootMotion.FinalIK;
using System.Reflection;
using UnityEngine;

namespace NAK.Melons.DesktopVRIK;

public static class VRIKUtils
{
    static readonly FieldInfo vrik_bendNormalRelToPelvis = typeof(IKSolverVR.Leg).GetField("bendNormalRelToPelvis", BindingFlags.NonPublic | BindingFlags.Instance);

    public static void ConfigureVRIKReferences(VRIK vrik, bool useVRIKToes, bool findUnmappedToes, out bool foundUnmappedToes)
    {
        foundUnmappedToes = false;

        //might not work over netik
        FixChestAndSpineReferences(vrik);

        if (!useVRIKToes)
        {
            vrik.references.leftToes = null;
            vrik.references.rightToes = null;
        }
        else if (findUnmappedToes)
        {
            //doesnt work with netik, but its toes...
            FindAndSetUnmappedToes(vrik, out foundUnmappedToes);
        }

        //bullshit fix to not cause death
        FixFingerBonesError(vrik);
    }

    private static void FixChestAndSpineReferences(VRIK vrik)
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

    private static void FindAndSetUnmappedToes(VRIK vrik, out bool foundUnmappedToes)
    {
        foundUnmappedToes = false;

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
                foundUnmappedToes = true;
            }
        }
    }

    private static Transform FindUnmappedToe(Transform foot)
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

    private static void FixFingerBonesError(VRIK vrik)
    {
        FixFingerBones(vrik, vrik.references.leftHand, vrik.solver.leftArm);
        FixFingerBones(vrik, vrik.references.rightHand, vrik.solver.rightArm);
    }

    private static void FixFingerBones(VRIK vrik, Transform hand, IKSolverVR.Arm armSolver)
    {
        if (hand.childCount == 0)
        {
            armSolver.wristToPalmAxis = Vector3.up;
            armSolver.palmToThumbAxis = hand == vrik.references.leftHand ? -Vector3.forward : Vector3.forward;
        }
    }

    public static void CalculateKneeBendNormals(VRIK vrik, out Vector3 leftKneeNormal, out Vector3 rightKneeNormal)
    {
        // Helper function to get position or default to Vector3.zero
        Vector3 GetPositionOrDefault(Transform transform) => transform?.position ?? Vector3.zero;

        // Get assumed left knee normal
        Vector3[] leftVectors = {
            GetPositionOrDefault(vrik.references.leftThigh),
            GetPositionOrDefault(vrik.references.leftCalf),
            GetPositionOrDefault(vrik.references.leftFoot)
        };
        leftKneeNormal = Quaternion.Inverse(vrik.references.root.rotation) * GetNormalFromArray(leftVectors);

        // Get assumed right knee normal
        Vector3[] rightVectors = {
            GetPositionOrDefault(vrik.references.rightThigh),
            GetPositionOrDefault(vrik.references.rightCalf),
            GetPositionOrDefault(vrik.references.rightFoot)
        };
        rightKneeNormal = Quaternion.Inverse(vrik.references.root.rotation) * GetNormalFromArray(rightVectors);
    }

    public static void ApplyKneeBendNormals(VRIK vrik, Vector3 leftKneeNormal, Vector3 rightKneeNormal)
    {
        // 0 uses bendNormalRelToPelvis, 1 is bendNormalRelToTarget
        // modifying pelvis normal weight is better math
        vrik.solver.leftLeg.bendToTargetWeight = 0f;
        vrik.solver.rightLeg.bendToTargetWeight = 0f;

        var pelvis_localRotationInverse = Quaternion.Inverse(vrik.references.pelvis.localRotation);
        var leftLeg_bendNormalRelToPelvis = pelvis_localRotationInverse * leftKneeNormal;
        var rightLeg_bendNormalRelToPelvis = pelvis_localRotationInverse * rightKneeNormal;

        vrik_bendNormalRelToPelvis.SetValue(vrik.solver.leftLeg, leftLeg_bendNormalRelToPelvis);
        vrik_bendNormalRelToPelvis.SetValue(vrik.solver.rightLeg, rightLeg_bendNormalRelToPelvis);
    }

    private static Vector3 GetNormalFromArray(Vector3[] positions)
    {
        Vector3 centroid = Vector3.zero;
        for (int i = 0; i < positions.Length; i++)
        {
            centroid += positions[i];
        }
        centroid /= positions.Length;

        Vector3 normal = Vector3.zero;
        for (int i = 0; i < positions.Length - 2; i++)
        {
            Vector3 side1 = positions[i] - centroid;
            Vector3 side2 = positions[i + 1] - centroid;
            normal += Vector3.Cross(side1, side2);
        }
        return normal.normalized;
    }

    public static void CalculateInitialIKScaling(VRIK vrik, out float initialFootDistance, out float initialStepThreshold, out float initialStepHeight)
    {
        // Get distance between feet and thighs
        float scaleModifier = Mathf.Max(1f, vrik.references.pelvis.lossyScale.x);
        float footDistance = Vector3.Distance(vrik.references.leftFoot.position, vrik.references.rightFoot.position);
        initialFootDistance = footDistance * 0.5f;
        initialStepThreshold = footDistance * scaleModifier;
        initialStepHeight = Vector3.Distance(vrik.references.leftFoot.position, vrik.references.leftCalf.position) * 0.2f;
    }

    public static void ApplyScaleToVRIK(VRIK vrik, float footDistance, float stepThreshold, float stepHeight, float modifier)
    {
        vrik.solver.locomotion.footDistance = footDistance * modifier;
        vrik.solver.locomotion.stepThreshold = stepThreshold * modifier;
        ScaleStepHeight(vrik.solver.locomotion.stepHeight, stepHeight * modifier);
    }

    private static void ScaleStepHeight(AnimationCurve stepHeightCurve, float mag)
    {
        Keyframe[] keyframes = stepHeightCurve.keys;
        keyframes[1].value = mag;
        stepHeightCurve.keys = keyframes;
    }

    public static void InitiateVRIKSolver(VRIK vrik)
    {
        vrik.solver.SetToReferences(vrik.references);
        vrik.solver.Initiate(vrik.transform);
    }
}