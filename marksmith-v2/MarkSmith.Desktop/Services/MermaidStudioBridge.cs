using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace MarkSmith.Services;

/// <summary>
/// Interop bridge for launching Visual Mermaid Studio window/dialog from WebView2 host objects or web messages.
/// </summary>
[ClassInterface(ClassInterfaceType.AutoDispatch)]
[ComVisible(true)]
public sealed class MermaidStudioBridge
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action<int, string> _launchCallback;

    public MermaidStudioBridge(DispatcherQueue dispatcherQueue, Action<int, string> launchCallback)
    {
        _dispatcherQueue = dispatcherQueue;
        _launchCallback = launchCallback;
    }

    public void LaunchStudio(int diagramIndex, string diagramCode)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _launchCallback(diagramIndex, diagramCode);
        });
    }
}
