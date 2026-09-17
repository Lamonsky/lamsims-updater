using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;
using Avalonia.Input.Platform;
using LamSims.App.Services;

namespace LamSims.App.Tests;

/// <summary>
/// Drives <see cref="AvaloniaClipboardService"/>'s internal seam directly, with a fake
/// <see cref="IClipboard"/> whose <c>SetDataAsync</c> is where a real one throws — see the seam's
/// own doc comment for the Windows call chain that lands there. Necessarily the internal overload,
/// not <c>LogViewModel.CopyCommand</c>'s <c>ExecuteAsync</c>: <c>FakeClipboard</c> and
/// <c>StubClipboard</c> are doubles for <see cref="IClipboardService"/>, the app-level interface —
/// a throw from either never reaches <see cref="AvaloniaClipboardService"/> at all, so a test built
/// on them could not tell a fixed <c>SetTextAsync</c> from an unfixed one; it would fail either way,
/// for a reason this fix cannot address.
///
/// <see cref="IClipboard"/> is marked <c>[NotClientImplementable]</c>, which Avalonia enforces with
/// a hidden interface member that makes an ordinary <c>class : IClipboard</c> a compile error (this
/// was confirmed the direct way, by writing one and watching CS0535 name the trick). A
/// <see cref="DispatchProxy"/> sidesteps that: the CLR type it emits at runtime implements every
/// member of the interface, including the hidden one, without a C# implementation for
/// <see cref="IClipboard"/> ever appearing in source.
/// </summary>
public sealed class AvaloniaClipboardServiceTests
{
    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(COMException))]
    public async Task SetTextAsync_does_not_throw_when_the_clipboard_does(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        var clipboard = ThrowingClipboard.Create(exception);

        // No AggregateException, no unhandled fault: the whole point of the fix is that this
        // completes rather than propagating what the clipboard threw.
        await AvaloniaClipboardService.SetTextAsync(clipboard, "log text");
    }

    [Fact]
    public async Task SetTextAsync_is_a_no_op_when_there_is_no_clipboard()
    {
        // The pre-existing guard, unaffected by the fix: still exercised here so a change to the
        // seam that broke it would fail alongside the throwing cases above.
        await AvaloniaClipboardService.SetTextAsync(null, "log text");
    }

    /// <summary>
    /// A minimal <see cref="IClipboard"/> whose <c>SetDataAsync</c> — what
    /// <c>IClipboard.SetTextAsync</c> calls straight through to — hands back a task already
    /// faulted with whatever <see cref="Create"/> was given, the same shape a real
    /// implementation's throw takes once it surfaces through an <c>await</c>.
    ///
    /// Not sealed: <see cref="DispatchProxy.Create{T,TProxy}"/> generates a type deriving from
    /// this one, which fails at runtime — not compile time — if it is.
    /// </summary>
    public class ThrowingClipboard : DispatchProxy
    {
        private Exception _toThrow = null!;

        public static IClipboard Create(Exception toThrow)
        {
            var proxy = Create<IClipboard, ThrowingClipboard>();

            // The compiler has no way to know the generated proxy also derives
            // ThrowingClipboard — that relationship exists only in the type DispatchProxy.Create
            // emits at runtime — so the cast has to go through object rather than directly.
            ((ThrowingClipboard)(object)proxy)._toThrow = toThrow;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(IClipboard.SetDataAsync) => Task.FromException(_toThrow),
                nameof(IClipboard.ClearAsync) or nameof(IClipboard.FlushAsync) => Task.CompletedTask,
                nameof(IClipboard.TryGetDataAsync) or nameof(IClipboard.TryGetInProcessDataAsync) =>
                    Task.FromResult<Avalonia.Input.IAsyncDataTransfer?>(null),
                _ => throw new NotSupportedException(
                    $"ThrowingClipboard has no stub for {targetMethod?.Name}."),
            };
    }
}
