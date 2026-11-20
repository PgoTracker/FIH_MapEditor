using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // "X-ray" view: wireframe boxes around colliders that have no visible mesh
    // (trigger zones, invisible walls, slide colliders) so they can be seen — and,
    // with "Pick invisible" ON, clicked — while editing.
    public class InvisibleVisualizer
    {
        public bool Enabled { get; private set; }
        public int Count => _entries.Count;
        public bool IsScanning => _scanColliders != null;

        private class Entry
        {
            public GameObject Root;
            public Bounds Bounds;
            public bool Trigger;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<LineBox> _boxes = new List<LineBox>();
        private Collider[] _scanColliders;
        private List<Entry> _pendingEntries;
        private Dictionary<int, Entry> _byRoot;
        private Dictionary<int, int> _climbCache;
        private int _scanIndex;
        private float _nextRefresh;

        // Big levels can have hundreds of invisible colliders: only draw the ones near
        // the camera, with a hard cap so the frame never drowns in LineRenderers.
        private const float SHOW_RANGE = 90f;
        private const int MAX_BOXES = 120;
        private const float REFRESH_INTERVAL = 8f;
        private const int SCAN_BATCH_SIZE = 64;
        private const double SCAN_BUDGET_MS = 2.0;

        private static readonly Color TriggerColor = new Color(1f, 0.35f, 1f, 0.85f);  // magenta: triggers
        private static readonly Color SolidColor = new Color(1f, 1f, 1f, 0.75f);       // white: invisible walls

        public void SetEnabled(bool on)
        {
            Enabled = on;
            if (on) Refresh();
            else
            {
                CancelScan();
                HideAll();
            }
        }

        public void OnSceneChanged()
        {
            CancelScan();
            _entries.Clear();
            _nextRefresh = 0f;
            HideAll();
        }

        // Scan the scene for collider-bearing objects with no enabled renderer anywhere
        // in their logical root. Re-run periodically: hides/unhides change the set.
        public void Refresh()
        {
            if (!Enabled || IsScanning) return;
            try
            {
                _scanColliders = UnityEngine.Object.FindObjectsOfType<Collider>();
                _pendingEntries = new List<Entry>();
                _byRoot = new Dictionary<int, Entry>();
                _climbCache = new Dictionary<int, int>();
                _scanIndex = 0;
            }
            catch (Exception ex)
            {
                CancelScan();
                _nextRefresh = Time.unscaledTime + REFRESH_INTERVAL;
                MapEditorPlugin.Logger.LogWarning($"[XRAY] Scan start error: {ex.Message}");
            }
        }

        private void ScanBatch()
        {
            if (!IsScanning) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            double budgetTicks = SCAN_BUDGET_MS * System.Diagnostics.Stopwatch.Frequency / 1000.0;
            try
            {
                int processed = 0;
                while (_scanIndex < _scanColliders.Length && processed < SCAN_BATCH_SIZE)
                {
                    var col = _scanColliders[_scanIndex++];
                    processed++;
                    if (col == null || !col.enabled) continue;
                    var t = col.transform;
                    if (t == null || t.name.StartsWith("FIH_")) continue;

                    var root = SelectionSystem.ClimbToLogicalRoot(t, _climbCache);
                    if (root == null || root.name.StartsWith("FIH_")) continue;

                    int id = root.GetInstanceID();
                    if (_byRoot.TryGetValue(id, out var entry))
                    {
                        if (entry != null)
                        {
                            var b = entry.Bounds;
                            b.Encapsulate(col.bounds);
                            entry.Bounds = b;
                            entry.Trigger |= col.isTrigger;
                        }
                    }
                    else
                    {
                        // A null cache entry means this root has a visible mesh or camera.
                        // Check its subtree once, even if many colliders share it.
                        if (root.GetComponentInChildren<Camera>(true) != null ||
                            ObjectCatalog.ComputeBounds(root.gameObject).size != Vector3.zero)
                            _byRoot[id] = null;
                        else
                        {
                            entry = new Entry { Root = root.gameObject, Bounds = col.bounds, Trigger = col.isTrigger };
                            _byRoot[id] = entry;
                            _pendingEntries.Add(entry);
                        }
                    }

                    if ((processed & 7) == 0 &&
                        System.Diagnostics.Stopwatch.GetTimestamp() - started >= budgetTicks)
                        break;
                }

                if (_scanIndex == _scanColliders.Length)
                {
                    _entries.Clear();
                    _entries.AddRange(_pendingEntries);
                    MapEditorPlugin.Logger.LogInfo($"[XRAY] {_entries.Count} invisible collider object(s) in the scene.");
                    CancelScan();
                    _nextRefresh = Time.unscaledTime + REFRESH_INTERVAL;
                }
            }
            catch (Exception ex)
            {
                CancelScan();
                _nextRefresh = Time.unscaledTime + REFRESH_INTERVAL;
                MapEditorPlugin.Logger.LogWarning($"[XRAY] Scan error: {ex.Message}");
            }
        }

        private void CancelScan()
        {
            _scanColliders = null;
            _pendingEntries = null;
            _byRoot = null;
            _climbCache = null;
            _scanIndex = 0;
        }

        // Call every frame in editor mode.
        public void Update(Camera cam)
        {
            if (!Enabled || cam == null) return;
            if (!IsScanning && Time.unscaledTime >= _nextRefresh) Refresh();
            ScanBatch();

            Vector3 camPos = cam.transform.position;
            int used = 0;
            foreach (var e in _entries)
            {
                if (used >= MAX_BOXES) break;
                if (e.Root == null || !e.Root.activeInHierarchy) continue;
                if (Vector3.Distance(camPos, e.Bounds.center) > SHOW_RANGE + e.Bounds.extents.magnitude)
                    continue;

                while (_boxes.Count <= used)
                    _boxes.Add(new LineBox($"FIH_Line_Xray{_boxes.Count}"));
                _boxes[used].ShowBox(e.Bounds, e.Trigger ? TriggerColor : SolidColor);
                used++;
            }
            for (int i = used; i < _boxes.Count; i++)
                _boxes[i].Hide();
        }

        public void HideAll()
        {
            foreach (var b in _boxes) b.Hide();
        }
    }
}
