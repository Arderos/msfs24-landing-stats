using System.Linq;
using System.Windows.Media.Media3D;
using MathPoint = LandingStats.App.ReplayMath.Point3D;
using MathVector = LandingStats.App.ReplayMath.Vector3D;
using MathQuaternion = LandingStats.App.ReplayMath.Quaternion;

namespace LandingStats.App.Controls;

internal static class ReplayWpfBridge
{
    internal static Point3D ToWpf(this MathPoint value) => new Point3D(value.X, value.Y, value.Z);
    internal static Vector3D ToWpf(this MathVector value) => new Vector3D(value.X, value.Y, value.Z);
    internal static Quaternion ToWpf(this MathQuaternion value) => new Quaternion(value.X, value.Y, value.Z, value.W);
    internal static MathPoint ToReplay(this Point3D value) => new MathPoint(value.X, value.Y, value.Z);
    internal static MathVector ToReplay(this Vector3D value) => new MathVector(value.X, value.Y, value.Z);
    internal static MeshGeometry3D ToWpf(this LandingStats.App.Models.ReplaySurfaceMesh value) => new MeshGeometry3D
    {
        Positions = new Point3DCollection(value.Positions.Select(point => point.ToWpf())),
        TriangleIndices = new System.Windows.Media.Int32Collection(value.TriangleIndices),
    };
}
