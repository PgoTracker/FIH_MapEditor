using System;
using System.Reflection;
using UnityEngine;
using GameGround = EHS.Ground.Ground;

namespace FIHMapEditor
{
    // Names and values match the game's Ground.SurfaceVisualType. Scripts use this
    // public enum instead of reaching into the game's private Ground setters.
    public enum PackagedGroundSurface
    {
        None = 0, SoftStone = 1, Grass = 2, SoftWood = 3, Mud = 4,
        SoftMetal = 5, Cardboard = 6, SoftPlastic = 7, Rope = 8,
        Sand = 9, Rubber = 10, Trash = 11, Snow = 12, Ice = 13,
        Empty = 14, HardWood = 15, HardMetal = 16, HardPlastic = 17,
        HardStone = 18, TODORelease = 19, Constructor = 20,
        PlasticLego = 21, MetalMagnetix = 22, Plushie = 23,
        Chocolate = 24, Candy = 25, Waffle = 26, IceCream = 27,
        Cookie = 28, Icing = 29,
    }

    public enum PackagedSpinVelocity
    {
        None = 0, Regular = 1, Slow = 2, AirBorn = 3,
    }

    public enum PackagedPhoneDestroy
    {
        Default = 0, DontDestroy = 1, DestroyImmediately = 2,
    }

    internal static class PackageGroundSettings
    {
        // A newly created Ground otherwise starts at None, which the game's
        // PlayerSettingsSO has no torque multiplier for. Scripts may override it.
        internal static void InitializeGround(GameGround ground)
            => Set(ground, "SpinVelocityType", EHS.Ground.SpinVelocityType.Regular);

        private static GameGround Ground(GameObject root)
            => GroundRegistrar.EnsurePackageGround(root)
               ?? throw new InvalidOperationException("Packaged object has no root for its Ground component.");

        private static void Set(GameGround ground, string name, object value)
        {
            var property = typeof(GameGround).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var setter = property?.GetSetMethod(true);
            if (setter == null) throw new MissingMethodException("Game Ground." + name + " setter was not found.");
            setter.Invoke(ground, new[] { value });
        }

        public static void Surface(GameObject root, PackagedGroundSurface surface)
        {
            if (!Enum.IsDefined(typeof(PackagedGroundSurface), surface)) throw new ArgumentOutOfRangeException(nameof(surface));
            Set(Ground(root), "SurfaceVisualType", (EHS.Ground.SurfaceVisualType)(int)surface);
        }

        public static void Spin(GameObject root, PackagedSpinVelocity spin)
        {
            if (!Enum.IsDefined(typeof(PackagedSpinVelocity), spin)) throw new ArgumentOutOfRangeException(nameof(spin));
            Set(Ground(root), "SpinVelocityType", (EHS.Ground.SpinVelocityType)(int)spin);
        }

        public static void PhoneDestroy(GameObject root, PackagedPhoneDestroy mode)
        {
            if (!Enum.IsDefined(typeof(PackagedPhoneDestroy), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            Set(Ground(root), "PhoneDestroyType", (EHS.PhoneDestroyType)(int)mode);
        }
    }
}
