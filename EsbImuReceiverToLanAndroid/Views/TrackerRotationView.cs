using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace EsbReceiverToLanAndroid.Views;

public class TrackerRotationView : GraphicsView
{
    public static readonly BindableProperty TrackerRotationProperty =
        BindableProperty.Create(nameof(TrackerRotation), typeof(Quaternion), typeof(TrackerRotationView), default(Quaternion), propertyChanged: OnRotationChanged);

    public Quaternion TrackerRotation
    {
        get => (Quaternion)GetValue(TrackerRotationProperty);
        set => SetValue(TrackerRotationProperty, value);
    }

    private const float RotationInvalidateThreshold = 0.001f;

    private static void OnRotationChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not TrackerRotationView view) return;
        if (oldValue is not Quaternion oldQ || newValue is not Quaternion newQ) return;
        // Skip invalidate if rotation barely changed (reduces persistent redraw storm)
        if (Quaternion.Dot(oldQ, newQ) < 1f - RotationInvalidateThreshold)
            view.Invalidate();
    }

    public TrackerRotationView()
    {
        HeightRequest = 40;
        WidthRequest = 40;
        Drawable = new TrackerRotationDrawable(this);
    }

        private class TrackerRotationDrawable : IDrawable
        {
            private readonly WeakReference<TrackerRotationView> _viewRef;
            private static int _drawCount;
            private static long _totalDrawTicks;
            private static readonly Stopwatch _sw = new();

            // Reused per-instance buffers (eliminates per-draw allocations)
            private readonly Vector3[] _rotated = new Vector3[8];
            private readonly PointF[] _projected = new PointF[8];
            private readonly (int A, int B, float Depth)[] _edgesWithDepth = new (int, int, float)[12];

            // Cube vertices (centered at origin, ±1)
            private static readonly Vector3[] CubeVertices =
            {
                new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1), // back face
                new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)     // front face
            };

            // 12 edges as vertex index pairs
            private static readonly (int A, int B)[] CubeEdges =
            {
                (0, 1), (1, 2), (2, 3), (3, 0),  // back
                (4, 5), (5, 6), (6, 7), (7, 4),  // front
                (0, 4), (1, 5), (2, 6), (3, 7)   // connecting
            };

            public TrackerRotationDrawable(TrackerRotationView view)
            {
                _viewRef = new WeakReference<TrackerRotationView>(view);
            }

            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                _sw.Restart();

                if (!_viewRef.TryGetTarget(out var view)) return;

                var q = view.TrackerRotation;
                if (q.LengthSquared() < 0.0001f) q = Quaternion.Identity;

                for (int i = 0; i < 8; i++)
                    _rotated[i] = Vector3.Transform(CubeVertices[i], q);

                var cx = dirtyRect.Width / 2;
                var cy = dirtyRect.Height / 2;
                var scale = MathF.Min(dirtyRect.Width, dirtyRect.Height) / 4.5f;

                for (int i = 0; i < 8; i++)
                {
                    var v = _rotated[i];
                    _projected[i] = new PointF(cx + v.X * scale, cy - v.Y * scale); // -Y so up is up on screen
                }

                // Manual depth extraction (no LINQ / no allocation)
                for (int i = 0; i < 12; i++)
                {
                    var e = CubeEdges[i];
                    var a = _rotated[e.A];
                    var b = _rotated[e.B];
                    _edgesWithDepth[i] = (e.A, e.B, (a.Z + b.Z) / 2);
                }

                // Manual insertion sort for 12 elements (extremely fast, zero alloc)
                for (int i = 1; i < 12; i++)
                {
                    var key = _edgesWithDepth[i];
                    int j = i - 1;
                    while (j >= 0 && _edgesWithDepth[j].Depth > key.Depth)
                    {
                        _edgesWithDepth[j + 1] = _edgesWithDepth[j];
                        j--;
                    }
                    _edgesWithDepth[j + 1] = key;
                }

                // Pre-calculate base white color to avoid per-iteration struct creation
                var white = Colors.White;
                for (int i = 0; i < 12; i++)
                {
                    var (a, b, depth) = _edgesWithDepth[i];
                    float alpha = 0.3f + 0.7f * (depth + 1) / 2; // depth -1..1 -> alpha 0.3..1
                    canvas.StrokeColor = new Color(white.Red, white.Green, white.Blue, alpha);
                    canvas.StrokeSize = 1.5f;
                    canvas.StrokeLineCap = LineCap.Round;
                    canvas.DrawLine(_projected[a], _projected[b]);
                }

                _sw.Stop();
                int count = Interlocked.Increment(ref _drawCount);
                long ticks = Interlocked.Add(ref _totalDrawTicks, _sw.ElapsedTicks);
                if (count >= 50)
                {
                    double avgMs = (ticks / (double)count) / TimeSpan.TicksPerMillisecond;
                    Console.WriteLine($"[PERF] TrackerRotationView.Draw avg {avgMs:F3} ms over {count} calls");
                    Interlocked.Exchange(ref _drawCount, 0);
                    Interlocked.Exchange(ref _totalDrawTicks, 0);
                }
            }
    }
}
