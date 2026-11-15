using FIHMapEditor;
using UnityEngine;

public class Spinner : IPackagedGeometryBehavior
{
    private bool _carrying;

    public void OnSpawn(PackagedGeometryContext context) { }

    public void Update(PackagedGeometryContext context, float deltaTime)
    {
        var root = context.Root;
        if (root == null) return;

        var player = context.LocalPlayer;
        Collider playerCollider = null;
        if (player != null)
            foreach (var collider in player.GetComponentsInChildren<Collider>(false))
                if (collider != null && collider.enabled && !collider.isTrigger)
                { playerCollider = collider; break; }
        var platformCollider = root.GetComponentInChildren<Collider>(false);
        bool standingOnTop = false;
        if (playerCollider != null && platformCollider != null)
        {
            var bounds = playerCollider.bounds;
            var ray = new Ray(bounds.center, Vector3.down);
            RaycastHit hit;
            standingOnTop = platformCollider.Raycast(ray, out hit, bounds.extents.y + 0.3f)
                && hit.normal.y > 0.5f
                && Mathf.Abs(bounds.min.y - hit.point.y) < 0.25f;
        }

        var pivot = root.transform.position;
        var turn = Quaternion.AngleAxis(45f * deltaTime, Vector3.up);
        root.transform.rotation = turn * root.transform.rotation;

        if (standingOnTop && !_carrying) Debug.Log("[PACKAGE] Spinner is carrying the local player.");
        _carrying = standingOnTop;
        if (!standingOnTop) return;
        var body = player.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.position = pivot + turn * (body.position - pivot);
            body.rotation = turn * body.rotation;
        }
        else
        {
            player.transform.position = pivot + turn * (player.transform.position - pivot);
            player.transform.rotation = turn * player.transform.rotation;
        }
    }

    public void OnDespawn(PackagedGeometryContext context) { }
}
