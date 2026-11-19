using System;
using System.Collections.Generic;
using System.Linq;

namespace Vector.Services;

public sealed class GestureMatcher
{
    private int _sensitivity;

    public GestureMatcher(int sensitivity = 50)
    {
        Sensitivity = sensitivity;
    }

    public int Sensitivity
    {
        get => _sensitivity;
        set => _sensitivity = Math.Clamp(value, 1, 100);
    }

    public bool TryMatchRectangle(
        IReadOnlyList<System.Windows.Point> points,
        out System.Windows.Rect bounds,
        out double score)
    {
        bounds = System.Windows.Rect.Empty;
        score = double.PositiveInfinity;
        if (points.Count < 4)
            return false;

        double left = points.Min(static p => p.X);
        double right = points.Max(static p => p.X);
        double top = points.Min(static p => p.Y);
        double bottom = points.Max(static p => p.Y);
        double width = right - left;
        double height = bottom - top;
        if (width < 20 || height < 20)
            return false;

        double diagonal = Math.Sqrt(width * width + height * height);
        double closeDx = points[^1].X - points[0].X;
        double closeDy = points[^1].Y - points[0].Y;
        if (Math.Sqrt(closeDx * closeDx + closeDy * closeDy) > diagonal * 0.45)
            return false;

        if (Math.Min(width, height) / Math.Max(width, height) < 0.08)
            return false;

        double pathLength = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double dx = points[i].X - points[i - 1].X;
            double dy = points[i].Y - points[i - 1].Y;
            pathLength += Math.Sqrt(dx * dx + dy * dy);
        }

        double perimeter = 2 * (width + height);
        if (pathLength < perimeter * 0.5 || pathLength > perimeter * 2.5)
            return false;

        double shortSide = Math.Min(width, height);
        var borderDistances = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            System.Windows.Point point = points[i];
            borderDistances[i] = Math.Min(
                Math.Min(point.X - left, right - point.X),
                Math.Min(point.Y - top, bottom - point.Y)) / shortSide;
        }

        Array.Sort(borderDistances);
        double meanDistance = borderDistances.Average();
        int p90Index = Math.Min(borderDistances.Length - 1, (int)(borderDistances.Length * 0.9));
        double tolerance = 0.12 + Sensitivity * 0.002;
        if (meanDistance > tolerance || borderDistances[p90Index] > tolerance * 1.8)
            return false;

        double cornerTolerance = 0.24 + Sensitivity * 0.0015;
        System.Windows.Point[] corners =
        [
            new(left, top),
            new(right, top),
            new(right, bottom),
            new(left, bottom)
        ];

        foreach (System.Windows.Point corner in corners)
        {
            double nearest = double.PositiveInfinity;
            foreach (System.Windows.Point point in points)
            {
                double dx = point.X - corner.X;
                double dy = point.Y - corner.Y;
                nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dy * dy));
            }

            if (nearest / diagonal > cornerTolerance)
                return false;
        }

        bounds = new System.Windows.Rect(left, top, width, height);
        score = meanDistance * 100;
        return true;
    }
}
