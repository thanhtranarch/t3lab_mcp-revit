using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using Autodesk.Revit.UI;

namespace T3LabRevit.Core
{
    /// <summary>
    /// Runs work on Revit's main thread inside a valid API context, from socket threads, through one
    /// ExternalEvent. Revit calls Execute when it is idle (not while a modal dialog or an edit mode is
    /// open), so a call waits at most startTimeoutMs to start and is then cancelled: a timed-out write
    /// never happens later. Must be created on the main thread in an API context (OnStartup, a command
    /// or an ExternalEvent): ExternalEvent.Create throws anywhere else.
    /// </summary>
    internal sealed class MainThread : IExternalEventHandler, IDisposable
    {
        const int Pending = 0, Running = 1, Cancelled = 2;
        const int MaxRunMs = 30 * 60 * 1000;

        readonly ConcurrentQueue<IWork> _queue = new ConcurrentQueue<IWork>();
        ExternalEvent _event;
        volatile bool _disposed;

        /// <summary>The UIApplication of the last Execute (main thread only).</summary>
        public static UIApplication App { get; private set; }

        public MainThread()
        {
            _event = ExternalEvent.Create(this);
        }

        public string GetName() => "T3Lab Revit MCP";

        public void Execute(UIApplication app)
        {
            App = app;
            // Raise() calls coalesce: drain everything queued so far.
            int guard = 0;
            while (!_disposed && guard++ < 256 && _queue.TryDequeue(out IWork work))
            {
                try { work.Execute(app); }
                catch (Exception ex) { Log.Error("main thread: " + ex); }
            }
            if (!_queue.IsEmpty && !_disposed) Raise();
        }

        void Raise()
        {
            try
            {
                ExternalEventRequest r = _event.Raise();
                if (r == ExternalEventRequest.Denied || r == ExternalEventRequest.TimedOut)
                    Log.Error("ExternalEvent.Raise: " + r);
            }
            catch (Exception ex) { Log.Error("ExternalEvent.Raise: " + ex.Message); }
        }

        /// <summary>Runs fn on the main thread; cancelled (never runs) if it has not started within startTimeoutMs.</summary>
        public T Run<T>(Func<UIApplication, T> fn, int startTimeoutMs = 60000)
        {
            if (_disposed) throw new ToolError("The T3Lab Revit MCP server is stopping. Retry in a moment.");
            var item = new WorkItem<T>(fn);
            _queue.Enqueue(item);
            Raise();
            if (!item.Done.Wait(startTimeoutMs))
            {
                if (Interlocked.CompareExchange(ref item.State, Cancelled, Pending) == Pending)
                    throw new NotStartedException($"Revit did not respond within {startTimeoutMs / 1000}s: a dialog, an edit mode (sketch, " +
                                        "in-place family) or a long operation is blocking it. Nothing was executed. Ask the user to " +
                                        "finish or cancel it in Revit, then retry.");
                item.Done.Wait(MaxRunMs); // it started: wait for it to finish
            }
            if (item.Error != null) ExceptionDispatchInfo.Capture(item.Error).Throw();
            return item.Result;
        }

        /// <summary>Queue work on the main thread and return at once (never waits, never throws).</summary>
        public void Post(Action<UIApplication> action)
        {
            if (_disposed) return;
            _queue.Enqueue(new WorkItem<int>(app => { action(app); return 0; }));
            Raise();
        }

        public void Dispose()
        {
            _disposed = true;
            try { _event?.Dispose(); } catch { }
            _event = null;
        }

        /// <summary>The work was cancelled before it started (Revit was not free in time): nothing ran.</summary>
        internal sealed class NotStartedException : Exception
        {
            public NotStartedException(string message) : base(message) { }
        }

        interface IWork
        {
            void Execute(UIApplication app);
        }

        sealed class WorkItem<T> : IWork
        {
            readonly Func<UIApplication, T> _fn;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public int State = Pending;
            public T Result;
            public Exception Error;

            public WorkItem(Func<UIApplication, T> fn)
            {
                _fn = fn;
            }

            public void Execute(UIApplication app)
            {
                if (Interlocked.CompareExchange(ref State, Running, Pending) != Pending) return;
                try { Result = _fn(app); }
                catch (Exception ex) { Error = ex; }
                finally { Done.Set(); }
            }
        }
    }
}
