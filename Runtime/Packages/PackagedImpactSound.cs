using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace FIHMapEditor
{
    // A script opts in with context.EnablePackagedImpactSound(). The matching WAV or
    // Ogg remains in the map package; FMOD receives a short-lived local extraction.
    public sealed class PackagedImpactSound : MonoBehaviour
    {
        private static bool _registered;

        [HideFromIl2Cpp]
        public static void EnsureRegistered()
        {
            if (_registered) return;
            ClassInjector.RegisterTypeInIl2Cpp<PackagedImpactSound>();
            _registered = true;
        }

        public PackagedImpactSound(IntPtr ptr) : base(ptr) { }

        private readonly List<FMOD.Channel> _channels = new List<FMOD.Channel>();
        private FMOD.Sound _sound;
        private string _filePath;
        private string _soundHash;
        private string _placementUid;
        private GameObjectFinder _finder;
        private MultiplayerSync _sync;
        private float _minImpulse;
        private float _cooldown;
        private float _volume;
        private bool _playerOnly;
        private float _lastPlay = float.NegativeInfinity;
        private bool _failed;

        [HideFromIl2Cpp]
        public void Configure(byte[] bytes, string extension, string placementUid,
            GameObjectFinder finder, MultiplayerSync sync, float minImpulse, float cooldown,
            float volume, bool playerOnly)
        {
            ReleaseSound();
            if (bytes == null || (extension != ".wav" && extension != ".ogg"))
                throw new ArgumentException("A packaged impact sound must be WAV or Ogg.");
            string folder = Path.Combine(Path.GetTempPath(), "FIHMapEditor", "Impacts");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(_filePath, bytes);
            _soundHash = Convert.ToHexString(SHA256.HashData(bytes));
            _placementUid = placementUid;
            _finder = finder;
            _sync = sync;
            _minImpulse = minImpulse;
            _cooldown = cooldown;
            _volume = volume;
            _playerOnly = playerOnly;
            _lastPlay = float.NegativeInfinity;
            _failed = false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_failed || _filePath == null || collision == null
                || collision.impulse.magnitude < _minImpulse
                || Time.unscaledTime - _lastPlay < _cooldown) return;
            bool localPlayer = IsLocalPlayerCollision(collision);
            bool playerCollision = localPlayer || IsPlayerCollision(collision);
            if (_playerOnly && !playerCollision) return;
            // Remote player replicas can collide on several clients. Their owning
            // client plays and announces the impact; replicas wait for that event.
            if (!localPlayer && _sync?.PeerCount > 0 && playerCollision) return;
            Vector3 point = collision.contactCount > 0
                ? collision.GetContact(0).point : transform.position;
            if (PlayAt(point))
            {
                _lastPlay = Time.unscaledTime;
                if (localPlayer) _sync?.SendPackagedImpact(_placementUid, _soundHash, point);
            }
        }

        private bool IsLocalPlayerCollision(Collision collision)
        {
            var other = collision.collider;
            var player = _finder?.FindPlayer();
            var playerBody = player != null ? player.GetComponent<Rigidbody>() : null;
            return other != null && playerBody != null
                && other.attachedRigidbody == playerBody;
        }

        private static bool IsPlayerCollision(Collision collision)
        {
            var other = collision.collider;
            if (other == null) return false;
            var body = other.attachedRigidbody;
            if (body == null) return false;
            var playerRef = body.GetComponentInParent<EHS.PlayerRef>();
            return playerRef != null && playerRef.Rb == body;
        }

        [HideFromIl2Cpp]
        public void PlayRemote(string soundHash, Vector3 point)
        {
            if (!string.Equals(_soundHash, soundHash, StringComparison.OrdinalIgnoreCase)) return;
            PlayAt(point);
        }

        private bool PlayAt(Vector3 point)
        {
            if (_failed || _filePath == null) return false;
            try
            {
                if (!FMODUnity.RuntimeManager.IsInitialized) return false;
                var core = FMODUnity.RuntimeManager.CoreSystem;
                if (!_sound.hasHandle())
                {
                    var created = core.createSound(_filePath,
                        FMOD.MODE.CREATESAMPLE | FMOD.MODE._3D | FMOD.MODE.LOOP_OFF, out _sound);
                    if (created != FMOD.RESULT.OK)
                    {
                        MapEditorPlugin.Logger.LogWarning($"[PACKAGE] Impact sound load failed: {created}");
                        _failed = true;
                        return false;
                    }
                }
                var result = core.playSound(_sound, default, true, out var channel);
                if (result != FMOD.RESULT.OK)
                {
                    MapEditorPlugin.Logger.LogWarning($"[PACKAGE] Impact sound playback failed: {result}");
                    _failed = true;
                    return false;
                }
                var position = new FMOD.VECTOR { x = point.x, y = point.y, z = point.z };
                var velocity = new FMOD.VECTOR();
                channel.set3DAttributes(ref position, ref velocity);
                channel.setVolume(_volume);
                channel.setPaused(false);
                _channels.Add(channel);
                if (_channels.Count > 16)
                {
                    _channels[0].stop();
                    _channels.RemoveAt(0);
                }
                return true;
            }
            catch (Exception ex)
            {
                _failed = true;
                MapEditorPlugin.Logger.LogWarning($"[PACKAGE] Impact sound error: {ex.Message}");
                return false;
            }
        }

        private void OnDestroy() => ReleaseSound();

        private void ReleaseSound()
        {
            foreach (var channel in _channels)
                if (channel.hasHandle()) channel.stop();
            _channels.Clear();
            if (_sound.hasHandle()) _sound.release();
            _sound = default;
            _soundHash = null;
            _placementUid = null;
            _finder = null;
            _sync = null;
            if (_filePath != null)
            {
                try { File.Delete(_filePath); } catch { }
                _filePath = null;
            }
        }
    }
}
