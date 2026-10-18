using System;

namespace LandingStats.App.ReplayMath;

// Double precision is intentional: geodetic projection subtracts Earth-scale coordinates.
public struct Point3D
{
    public Point3D(double x, double y, double z) { X = x; Y = y; Z = z; }
    public double X, Y, Z;
    public static bool operator ==(Point3D a, Point3D b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
    public static bool operator !=(Point3D a, Point3D b) => !(a == b);
    public override bool Equals(object? value) => value is Point3D other && this == other;
    public override int GetHashCode() => X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode();
    public static Point3D operator +(Point3D p, Vector3D v) => new Point3D(p.X + v.X, p.Y + v.Y, p.Z + v.Z);
    public static Point3D operator -(Point3D p, Vector3D v) => new Point3D(p.X - v.X, p.Y - v.Y, p.Z - v.Z);
    public static Vector3D operator -(Point3D a, Point3D b) => new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}
public struct Vector3D
{
    public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }
    public double X, Y, Z;
    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);
    public void Normalize() { var length = Length; X /= length; Y /= length; Z /= length; }
    public static Vector3D CrossProduct(Vector3D a, Vector3D b) => new Vector3D(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double DotProduct(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3D operator +(Vector3D a, Vector3D b) => new Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(Vector3D a, Vector3D b) => new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3D operator -(Vector3D a) => new Vector3D(-a.X, -a.Y, -a.Z);
    public static Vector3D operator *(Vector3D a, double value) => new Vector3D(a.X * value, a.Y * value, a.Z * value);
    public static Vector3D operator /(Vector3D a, double value) => new Vector3D(a.X / value, a.Y / value, a.Z / value);
}
public struct Quaternion
{
    public Quaternion(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
    public Quaternion(Vector3D axis, double degrees)
    {
        axis.Normalize(); var half = degrees * Math.PI / 360; var sine = Math.Sin(half);
        X = axis.X * sine; Y = axis.Y * sine; Z = axis.Z * sine; W = Math.Cos(half);
    }
    public double X, Y, Z, W;
    public void Normalize() { var length = Math.Sqrt(X * X + Y * Y + Z * Z + W * W); X /= length; Y /= length; Z /= length; W /= length; }
    public static bool operator ==(Quaternion a, Quaternion b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W;
    public static bool operator !=(Quaternion a, Quaternion b) => !(a == b);
    public override bool Equals(object? value) => value is Quaternion other && this == other;
    public override int GetHashCode() => X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode() ^ W.GetHashCode();
    public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    public static Quaternion Slerp(Quaternion a, Quaternion b, double fraction, bool shortest)
    {
        var dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        if (shortest && dot < 0) { b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W); dot = -dot; }
        dot = Math.Max(-1, Math.Min(1, dot));
        double left, right;
        if (dot > .999999) { left = 1 - fraction; right = fraction; }
        else { var angle = Math.Acos(dot); var sine = Math.Sin(angle); left = Math.Sin((1 - fraction) * angle) / sine; right = Math.Sin(fraction * angle) / sine; }
        var result = new Quaternion(left * a.X + right * b.X, left * a.Y + right * b.Y, left * a.Z + right * b.Z, left * a.W + right * b.W);
        result.Normalize(); return result;
    }
    public Vector3D Transform(Vector3D value)
    {
        var axis = new Vector3D(X, Y, Z);
        var cross = Vector3D.CrossProduct(axis, value) * 2;
        return value + cross * W + Vector3D.CrossProduct(axis, cross);
    }
}
internal struct Matrix3D
{
    private Quaternion _rotation;
    private Vector3D _translation;
    internal static Matrix3D Identity => new Matrix3D { _rotation = new Quaternion(0, 0, 0, 1) };
    internal void Rotate(Quaternion rotation) { _rotation = rotation; }
    internal void Translate(Vector3D translation) { _translation += translation; }
    internal Point3D Transform(Point3D point)
    {
        var vector = _rotation.Transform(new Vector3D(point.X, point.Y, point.Z)) + _translation;
        return new Point3D(vector.X, vector.Y, vector.Z);
    }
}
