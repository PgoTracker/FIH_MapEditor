using System;
using System.Collections.Generic;
using System.IO;
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
        private float _minImpulse;
        private float _cooldown;
        private float _volume;
        private float _lastPlay = float.NegativeInfinity;
        private bool _failed;

        [HideFromIl2Cpp]
        public void Configure(byte[] bytes, string extension, float minImpulse, float cooldown, float volume)
        {
            ReleaseSound();
            if (bytes == null || (extension != ".wav" && extension != ".ogg"))
                throw new ArgumentException("A packaged impact sound must be WAV or Ogg.");
            string folder = Path.Combine(Path.GetTempPath(), "FIHMapEditor", "Impacts");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(_filePath, bytes);
            _minImpulse = minImpulse;
            _cooldown = cooldown;
            _volume = volume;
            _lastPlay = float.NegativeInfinity;
            _failed = false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_failed || _filePath == null || collision == null
                || collision.impulse.magnitude < _minImpulse
                || Time.unscaledTime - _lastPlay < _cooldown) return;
            try
            {
                if (!FMODUnity.RuntimeManager.IsInitialized) return;
                var core = FMODUnity.RuntimeManager.CoreSystem;
                if (!_sound.hasHandle())
                {
                    var created = core.createSound(_filePath,
                        FMOD.MODE.CREATESAMPLE | FMOD.MODE._3D | FMOD.MODE.LOOP_OFF, out _sound);
                    if (created != FMOD.RESULT.OK)
                    {
                        MapEditorPlugin.Logger.LogWarning($"[PACKAGE] Impact sound load failed: {created}");
                        _failed = true;
                        return;
                    }
                }
                var result = core.playSound(_sound, default, true, out var channel);
                if (result != FMOD.RESULT.OK)
                {
                    MapEditorPlugin.Logger.LogWarning($"[PACKAGE] Impact sound playback failed: {result}");
                    _failed = true;
                    return;
                }
                Vector3 point = collision.contactCount > 0
                    ? collision.GetContact(0).point : transform.position;
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
                _lastPlay = Time.unscaledTime;
            }
            catch (Exception ex)
            {
                _failed = true;
                MapEditorPlugin.Logger.LogWarning($"[PACKAGE] Impact sound error: {ex.Message}");
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
            if (_filePath != null)
            {
                try { File.Delete(_filePath); } catch { }
                _filePath = null;
            }
        }
    }
}
