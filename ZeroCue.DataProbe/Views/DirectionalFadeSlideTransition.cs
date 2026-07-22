using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Threading;
using ZeroCue.DataProbe.ViewModels;

namespace ZeroCue.DataProbe.Views;

public class DirectionalFadeSlideTransition : AvaloniaObject, IPageTransition
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(260);

    public double SlideDistance { get; set; } = 44;

    public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (Duration <= TimeSpan.Zero || MainViewModel.SuppressRemapInputTabTransition)
        {
            ResetVisual(from, null);
            ResetVisual(to, null);
            return Task.CompletedTask;
        }

        var direction = MainViewModel.RemapInputTabTransitionDirection >= 0 ? 1 : -1;
        var fromTransform = from?.RenderTransform;
        var toTransform = to?.RenderTransform;
        var fromSlide = from is null ? null : new TranslateTransform();
        var toSlide = to is null ? null : new TranslateTransform(SlideDistance * direction, 0);

        if (from is not null)
        {
            from.Opacity = 1;
            from.RenderTransform = fromSlide;
        }

        if (to is not null)
        {
            to.Opacity = 0;
            to.RenderTransform = toSlide;
        }

        var start = DateTime.UtcNow;
        var completion = new TaskCompletionSource<object?>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        CancellationTokenRegistration cancellationRegistration = default;

        void Complete()
        {
            timer.Stop();
            cancellationRegistration.Dispose();
            ResetVisual(from, fromTransform);
            ResetVisual(to, toTransform);
            completion.TrySetResult(null);
        }

        timer.Tick += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Complete();
                return;
            }

            var elapsed = DateTime.UtcNow - start;
            var rawProgress = Math.Clamp(elapsed.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
            var progress = SmoothStep(rawProgress);

            if (from is not null && fromSlide is not null)
            {
                from.Opacity = 1 - progress;
                fromSlide.X = -SlideDistance * direction * progress;
            }

            if (to is not null && toSlide is not null)
            {
                to.Opacity = progress;
                toSlide.X = SlideDistance * direction * (1 - progress);
            }

            if (rawProgress >= 1)
            {
                Complete();
            }
        };

        cancellationRegistration = cancellationToken.Register(Complete);
        timer.Start();

        return completion.Task;
    }

    private static double SmoothStep(double value)
    {
        return value * value * (3 - 2 * value);
    }

    private static void ResetVisual(Visual? visual, ITransform? transform)
    {
        if (visual is null)
        {
            return;
        }

        visual.Opacity = 1;
        visual.RenderTransform = transform;
    }
}
