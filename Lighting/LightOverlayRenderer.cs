using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace IzzysFurniture;

internal static class LightOverlayRenderer
{
    public static void Draw(SpawnedFurniture item, Matrix4x4 transform, Matrix4x4 view, Matrix4x4 projection, Vector2 displaySize)
    {
        var settings = item.Light!.Settings;
        var color = ImGui.GetStyle().Colors[(int)ImGuiCol.CheckMark];
        color.W = 0.9f;
        var lineColor = ImGui.GetColorU32(color);
        color.W = 0.3f;
        var softColor = ImGui.GetColorU32(color);

        var canvas = new OverlayCanvas(ImGui.GetWindowDrawList(), view * projection, displaySize);
        var origin = item.Position;
        var localX = Vector3.Normalize(new Vector3(transform.M11, transform.M12, transform.M13));
        var localY = Vector3.Normalize(new Vector3(transform.M21, transform.M22, transform.M23));
        var localZ = Vector3.Normalize(new Vector3(transform.M31, transform.M32, transform.M33));

        if (settings.Type == SceneLightType.Point)
        {
            canvas.Circle(origin, localX, localY, settings.Range, lineColor);
            canvas.Circle(origin, localY, localZ, settings.Range, lineColor);
            canvas.Circle(origin, localZ, localX, settings.Range, lineColor);
            return;
        }

        if (settings.Type == SceneLightType.Spot)
        {
            canvas.Cone(origin, localX, localY, localZ, settings.Range, settings.SpotAngle, lineColor);
            canvas.Cone(origin, localX, localY, localZ, settings.Range, settings.SpotAngle + settings.AngularFalloff, softColor);
            return;
        }

        var halfX = localX * MathF.Abs(item.Scale3.X) * 0.5f;
        var halfY = localY * MathF.Abs(item.Scale3.Y) * 0.5f;
        canvas.Quad(origin, halfX, halfY, lineColor);

        // flat lights project along local z, with the two skew angles moving the far plane
        var skew = localX * settings.Range * MathF.Tan(float.DegreesToRadians(settings.AreaSkew.Y))
            - localY * settings.Range * MathF.Tan(float.DegreesToRadians(settings.AreaSkew.X));
        var farCenter = origin + localZ * settings.Range + skew;
        canvas.Quad(farCenter, halfX, halfY, lineColor);
        canvas.Line(origin + halfX + halfY, farCenter + halfX + halfY, lineColor);
        canvas.Line(origin + halfX - halfY, farCenter + halfX - halfY, lineColor);
        canvas.Line(origin - halfX - halfY, farCenter - halfX - halfY, lineColor);
        canvas.Line(origin - halfX + halfY, farCenter - halfX + halfY, lineColor);
        canvas.Arrow(origin, farCenter, localX, localY, lineColor);
    }

    private readonly struct OverlayCanvas
    {
        private readonly ImDrawListPtr drawList;
        private readonly Matrix4x4 viewProjection;
        private readonly Vector2 displaySize;

        public OverlayCanvas(ImDrawListPtr drawList, Matrix4x4 viewProjection, Vector2 displaySize)
        {
            this.drawList = drawList;
            this.viewProjection = viewProjection;
            this.displaySize = displaySize;
        }

        public void Cone(Vector3 origin, Vector3 localX, Vector3 localY, Vector3 localZ, float range, float angleDegrees, uint color)
        {
            var visibleAngle = Math.Clamp(angleDegrees, 0.0f, 179.0f);
            var radiusPerUnit = MathF.Tan(float.DegreesToRadians(visibleAngle * 0.5f));

            ReadOnlySpan<float> slices = [0.2f, 0.5f, 1.0f];
            foreach (var ratio in slices)
            {
                var distance = range * ratio;
                this.Circle(origin + localZ * distance, localX, localY, distance * radiusPerUnit, color);
            }

            for (var spoke = 0; spoke < 8; spoke++)
            {
                var angle = spoke / 8.0f * MathF.Tau;
                var rim = origin + localZ * range
                    + (MathF.Cos(angle) * localX + MathF.Sin(angle) * localY) * range * radiusPerUnit;
                this.Line(origin, rim, color);
            }
        }

        public void Circle(Vector3 center, Vector3 axisOne, Vector3 axisTwo, float radius, uint color)
        {
            const int segments = 48;
            var previous = center + axisOne * radius;
            for (var segment = 1; segment <= segments; segment++)
            {
                var angle = segment / (float)segments * MathF.Tau;
                var current = center + (MathF.Cos(angle) * axisOne + MathF.Sin(angle) * axisTwo) * radius;
                this.Line(previous, current, color);
                previous = current;
            }
        }

        public void Quad(Vector3 center, Vector3 halfX, Vector3 halfY, uint color)
        {
            this.Line(center + halfX + halfY, center + halfX - halfY, color);
            this.Line(center + halfX - halfY, center - halfX - halfY, color);
            this.Line(center - halfX - halfY, center - halfX + halfY, color);
            this.Line(center - halfX + halfY, center + halfX + halfY, color);
        }

        public void Arrow(Vector3 start, Vector3 end, Vector3 localX, Vector3 localY, uint color)
        {
            this.Line(start, end, color);
            var length = Vector3.Distance(start, end);
            if (length < 0.0001f)
                return;

            var direction = (end - start) / length;
            var head = Math.Clamp(length * 0.12f, 0.15f, 0.45f);
            var headCenter = end - direction * head;
            this.Line(end, headCenter + localX * head * 0.55f, color);
            this.Line(end, headCenter - localX * head * 0.55f, color);
            this.Line(end, headCenter + localY * head * 0.55f, color);
            this.Line(end, headCenter - localY * head * 0.55f, color);
        }

        public void Line(Vector3 start, Vector3 end, uint color)
        {
            if (this.Project(start, out var screenStart) && this.Project(end, out var screenEnd))
                this.drawList.AddLine(screenStart, screenEnd, color, 2.0f);
        }

        private bool Project(Vector3 world, out Vector2 screen)
        {
            var clip = Vector4.Transform(new Vector4(world, 1.0f), this.viewProjection);
            if (clip.W <= 0.0001f)
            {
                screen = Vector2.Zero;
                return false;
            }

            clip /= clip.W;
            if (clip.Z < 0.0f || clip.Z > 1.0f)
            {
                screen = Vector2.Zero;
                return false;
            }

            // the draw list clips off-screen line ends, which keeps large range guides visible
            screen = new Vector2(
                (clip.X + 1.0f) * this.displaySize.X * 0.5f,
                (1.0f - clip.Y) * this.displaySize.Y * 0.5f);
            return true;
        }
    }
}
