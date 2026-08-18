using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

public sealed class RecoveryTestAgentController : AgentControllerBase
{
    public void ConfigureForTest(Rigidbody body, GameObject leftHand, GameObject rightHand)
    {
        rigidbody = body;
        InitializeHandRuntime(AgentHandSide.Left, leftHand, null, null, null);
        InitializeHandRuntime(AgentHandSide.Right, rightHand, null, null, null);
    }
}

public class AgentOutOfBoundsRecoveryTests
{
    private GameObject _agentObject;
    private GameObject _floorObject;
    private GameObject _leftHand;
    private GameObject _rightHand;
    private RecoveryTestAgentController _agent;
    private Rigidbody _body;

    [SetUp]
    public void SetUp()
    {
        _agentObject = new GameObject("Recovery test agent");
        _body = _agentObject.AddComponent<Rigidbody>();
        _body.useGravity = false;

        _leftHand = new GameObject("Left hand");
        _leftHand.transform.SetParent(_agentObject.transform, false);
        _rightHand = new GameObject("Right hand");
        _rightHand.transform.SetParent(_agentObject.transform, false);

        _agent = _agentObject.AddComponent<RecoveryTestAgentController>();
        _agent.ConfigureForTest(_body, _leftHand, _rightHand);

        _floorObject = new GameObject("Recovery test floor");
        SetPrivateField("_floorTransform", _floorObject.transform);
        SetPrivateField("_floorLocalBounds", new Bounds(Vector3.zero, new Vector3(10f, 0.2f, 10f)));
        SetPrivateField("_spawnPosition", new Vector3(1f, 0f, 1f));
        SetPrivateField("_hasFloorBounds", true);
        SetPrivateField("floorBoundsPadding", 0f);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_agentObject);
        Object.DestroyImmediate(_floorObject);
    }

    [Test]
    public void HorizontalEscape_RecoversOnce_AndPreservesHeadingAndHeldItem()
    {
        _agentObject.transform.position = new Vector3(6f, 0f, 0f);
        _agentObject.transform.rotation = Quaternion.Euler(0f, 37f, 0f);
        _body.linearVelocity = new Vector3(3f, 0f, 2f);
        _body.angularVelocity = new Vector3(0f, 4f, 0f);

        GameObject heldObject = new GameObject("Held retail item");
        heldObject.transform.SetParent(_rightHand.transform, false);
        SetHandRuntimeField("_rightHand", "HeldItem", new RuntimeRetailItem(
            "test-item", heldObject, RetailItemRuntimeState.Held));
        SetHandRuntimeField("_rightHand", "IsGripped", true);

        int eventCount = 0;
        int reportedCount = -1;
        _agent.OutOfBoundsRecovered += (_controller, count, _position, _rotation) =>
        {
            eventCount++;
            reportedCount = count;
        };

        Assert.That(_agent.RecoverIfOutOfBounds(), Is.True);
        Assert.That(_body.position, Is.EqualTo(new Vector3(1f, 0f, 1f)));
        Assert.That(Quaternion.Angle(_agentObject.transform.rotation, Quaternion.Euler(0f, 37f, 0f)),
            Is.LessThan(0.001f));
        Assert.That(_body.linearVelocity, Is.EqualTo(Vector3.zero));
        Assert.That(_body.angularVelocity, Is.EqualTo(Vector3.zero));
        Assert.That(_agent.IsHoldingItem(AgentHandSide.Right), Is.True);
        Assert.That(heldObject.transform.parent, Is.EqualTo(_rightHand.transform));
        Assert.That(_agent.OutOfBoundsRecoveryCount, Is.EqualTo(1));
        Assert.That(eventCount, Is.EqualTo(1));
        Assert.That(reportedCount, Is.EqualTo(1));

        Assert.That(_agent.RecoverIfOutOfBounds(), Is.False);
        Assert.That(_agent.OutOfBoundsRecoveryCount, Is.EqualTo(1));
        Assert.That(eventCount, Is.EqualTo(1));
    }

    [Test]
    public void FallingBelowFloor_RecoversToSpawn()
    {
        _agentObject.transform.position = new Vector3(0f, -1f, 0f);

        Assert.That(_agent.RecoverIfOutOfBounds(), Is.True);
        Assert.That(_body.position, Is.EqualTo(new Vector3(1f, 0f, 1f)));
        Assert.That(_agent.OutOfBoundsRecoveryCount, Is.EqualTo(1));
    }

    [Test]
    public void NonFinitePosition_IsClassifiedOutOfBounds()
    {
        MethodInfo method = typeof(AgentControllerBase).GetMethod(
            "IsOutsideFloorBounds", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(method, Is.Not.Null);
        Assert.That((bool)method.Invoke(_agent, new object[]
        {
            new Vector3(float.NaN, 0f, 0f)
        }), Is.True);
        Assert.That((bool)method.Invoke(_agent, new object[]
        {
            new Vector3(0f, float.PositiveInfinity, 0f)
        }), Is.True);
    }

    [Test]
    public void Recovery_ReleasesDoorConstraint_WithoutResettingHandPose()
    {
        GameObject doorObject = new GameObject("Door");
        Rigidbody doorBody = doorObject.AddComponent<Rigidbody>();
        doorBody.useGravity = false;
        doorObject.AddComponent<HingeJoint>();
        GameObject handleObject = new GameObject("Door handle");
        handleObject.transform.SetParent(doorObject.transform, false);
        DoorHandle doorHandle = handleObject.AddComponent<DoorHandle>();

        Vector3 handPose = new Vector3(0.2f, 0.3f, 0.4f);
        _leftHand.transform.localPosition = handPose;
        SetHandRuntimeField("_leftHand", "GrabbedDoor", doorHandle);
        SetHandRuntimeField("_leftHand", "IsGripped", true);
        _agentObject.transform.position = new Vector3(6f, 0f, 0f);

        Assert.That(_agent.RecoverIfOutOfBounds(), Is.True);
        Assert.That(GetHandRuntimeField("_leftHand", "GrabbedDoor"), Is.Null);
        Assert.That(_agent.IsLeftGripped, Is.False);
        Assert.That(_leftHand.transform.localPosition, Is.EqualTo(handPose));

        Object.DestroyImmediate(doorObject);
    }

    [Test]
    public void V1Reply_HasTwoCoordinateTuples_AndParseSafeCounterLine()
    {
        string response = SariAgentCommandBehavior.FormatV1AgentState(
            new Vector3(1f, 2f, 3f),
            new Vector3(4f, 5f, 6f),
            false,
            7);

        Assert.That(Regex.Matches(response, @"\([^\r\n()]*\)"), Has.Count.EqualTo(2));
        Assert.That(response.Split('\n'), Has.Length.EqualTo(4));
        StringAssert.EndsWith("Out-of-bounds recovery count: 7", response);
    }

    private void SetPrivateField(string name, object value)
    {
        FieldInfo field = typeof(AgentControllerBase).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field {name}");
        field.SetValue(_agent, value);
    }

    private void SetHandRuntimeField(string runtimeName, string fieldName, object value)
    {
        object runtime = GetHandRuntime(runtimeName);
        FieldInfo field = runtime.GetType().GetField(
            fieldName, BindingFlags.Instance | BindingFlags.Public);
        Assert.That(field, Is.Not.Null, $"Missing hand runtime field {fieldName}");
        field.SetValue(runtime, value);
    }

    private object GetHandRuntimeField(string runtimeName, string fieldName)
    {
        object runtime = GetHandRuntime(runtimeName);
        FieldInfo field = runtime.GetType().GetField(
            fieldName, BindingFlags.Instance | BindingFlags.Public);
        Assert.That(field, Is.Not.Null, $"Missing hand runtime field {fieldName}");
        return field.GetValue(runtime);
    }

    private object GetHandRuntime(string runtimeName)
    {
        FieldInfo field = typeof(AgentControllerBase).GetField(
            runtimeName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing hand runtime {runtimeName}");
        return field.GetValue(_agent);
    }
}
