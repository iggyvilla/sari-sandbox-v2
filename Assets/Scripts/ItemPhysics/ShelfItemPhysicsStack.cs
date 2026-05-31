using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Runtime-only coordinator shared by one vertical column of shelf items.
// Members are registered lowest-to-highest so the physics support chain exists before
// Unity advances the simulation.
public sealed class ShelfItemPhysicsStack
{
    private const float MaxSettleWaitSeconds = 2f;
    private const float SettlePollIntervalSeconds = 0.1f;

    private readonly List<ItemBBoxPhysicsProxy> _members = new();
    private readonly Dictionary<ItemBBoxPhysicsProxy, HashSet<Collider>> _handOverlaps = new();

    private Coroutine _settleCoroutine;
    private bool _permanentlyPhysical;
    private bool _isActivating;

    public ShelfItemPhysicsStack(IReadOnlyList<ItemBBoxInfo> orderedMembers)
    {
        foreach (ItemBBoxInfo bboxInfo in orderedMembers)
        {
            if (bboxInfo == null) continue;

            bboxInfo.PhysicsStack = this;

            ItemBBoxPhysicsProxy proxy = bboxInfo.GetComponent<ItemBBoxPhysicsProxy>();
            if (proxy != null)
                _members.Add(proxy);
        }
    }

    public bool ActivatePhysicsPreviews(bool settleWhenUnoccupied = false)
    {
        if (_permanentlyPhysical || _isActivating) return true;

        CancelSettleEvaluation();
        _isActivating = true;

        foreach (ItemBBoxPhysicsProxy proxy in _members)
        {
            if (proxy == null || proxy.EnsurePhysicsPreview()) continue;

            RollBackPhysicsPreviews();
            _isActivating = false;
            return false;
        }

        _isActivating = false;

        if (settleWhenUnoccupied && !HasHandOverlaps())
            BeginSettleEvaluation();

        return true;
    }

    internal void OnHandEnter(ItemBBoxPhysicsProxy proxy, Collider handCollider)
    {
        if (_permanentlyPhysical) return;

        if (!_handOverlaps.TryGetValue(proxy, out HashSet<Collider> colliders))
        {
            colliders = new HashSet<Collider>();
            _handOverlaps[proxy] = colliders;
        }

        colliders.Add(handCollider);
        CancelSettleEvaluation();
        ActivatePhysicsPreviews();
    }

    internal void OnHandExit(ItemBBoxPhysicsProxy proxy, Collider handCollider)
    {
        if (_permanentlyPhysical) return;

        if (_handOverlaps.TryGetValue(proxy, out HashSet<Collider> colliders))
        {
            colliders.Remove(handCollider);
            if (colliders.Count == 0)
                _handOverlaps.Remove(proxy);
        }

        if (!HasHandOverlaps())
            BeginSettleEvaluation();
    }

    internal void OnMemberRemoved(ItemBBoxPhysicsProxy proxy)
    {
        _handOverlaps.Remove(proxy);
        _members.Remove(proxy);

        if (!_isActivating && !HasHandOverlaps())
            BeginSettleEvaluation();
    }

    private void BeginSettleEvaluation()
    {
        if (_settleCoroutine != null || _permanentlyPhysical || !HasActivePhysicsPreviews())
            return;

        _settleCoroutine = RetailItemRuntimeService.Instance.StartCoroutine(WaitAndEvaluate());
    }

    private void CancelSettleEvaluation()
    {
        if (_settleCoroutine == null) return;

        RetailItemRuntimeService.Instance.StopCoroutine(_settleCoroutine);
        _settleCoroutine = null;
    }

    private IEnumerator WaitAndEvaluate()
    {
        yield return null;

        float elapsed = 0f;
        while (HasAwakePhysicsPreviews() && elapsed < MaxSettleWaitSeconds)
        {
            yield return new WaitForSeconds(SettlePollIntervalSeconds);
            elapsed += SettlePollIntervalSeconds;
        }

        bool shouldStayPhysical = false;
        foreach (ItemBBoxPhysicsProxy proxy in _members)
        {
            if (proxy != null && proxy.HasPhysicsPreviewMovedPastThreshold())
            {
                shouldStayPhysical = true;
                break;
            }
        }

        if (shouldStayPhysical)
        {
            _permanentlyPhysical = true;
            foreach (ItemBBoxPhysicsProxy proxy in _members)
                proxy?.MarkPhysicsPreviewAsDropped();
        }
        else
        {
            foreach (ItemBBoxPhysicsProxy proxy in _members)
                proxy?.RestorePhysicsPreviewToShelf();
        }

        _settleCoroutine = null;
    }

    private bool HasActivePhysicsPreviews()
    {
        foreach (ItemBBoxPhysicsProxy proxy in _members)
            if (proxy != null && proxy.HasPhysicsPreview)
                return true;

        return false;
    }

    private bool HasAwakePhysicsPreviews()
    {
        foreach (ItemBBoxPhysicsProxy proxy in _members)
            if (proxy != null && proxy.HasPhysicsPreview && !proxy.IsPhysicsPreviewSleeping())
                return true;

        return false;
    }

    private bool HasHandOverlaps()
    {
        return _handOverlaps.Count > 0;
    }

    private void RollBackPhysicsPreviews()
    {
        foreach (ItemBBoxPhysicsProxy proxy in _members)
            proxy?.RestorePhysicsPreviewToShelf();
    }
}
