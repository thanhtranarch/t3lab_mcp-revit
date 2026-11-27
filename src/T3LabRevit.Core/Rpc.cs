using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace T3LabRevit.Core
{
    /// <summary>Handles one request line from the bridge. Methods: health, tools/list, tools/call.</summary>
    internal static class Rpc
    {
        public static string Handle(string line)
        {
            Dictionary<string, object> request;
            try { request = Json.Parse(line) as Dictionary<string, object>; }
            catch { return null; }
            if (request == null || !request.ContainsKey("method")) return null;

            request.TryGetValue("id", out object id);
            string method = Convert.ToString(request["method"]);
            request.TryGetValue("params", out object rawParams);
            var p = new Args(rawParams);

            try
            {
                object result;
                switch (method)
                {
                    case "health": result = Health(); break;
                    case "tools/list": result = Registry.ListJson(); break;
                    case "tools/call":
                        result = CallTool(p.ReqStr("name"), new Args(p.Raw("arguments")), AgentCursors.ClientKey(p.Raw("client")),
                            p.Str("lane"), p.Num("queue_wait_s", 0));
                        break;
                    default: return Error(id, -32601, "Unknown method: " + method);
                }
                return Json.Write(new Dictionary<string, object> { ["id"] = id, ["result"] = result });
            }
            catch (Exception ex)
            {
                Log.Error(method + ": " + ex);
                return Error(id, -32603, ex.Message);
            }
        }

        static string Error(object id, int code, string message) =>
            Json.Write(new Dictionary<string, object>
            {
                ["id"] = id,
                ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message },
            });

        /// <summary>Answered on the socket thread (never waits for Revit), so the bridge can probe a busy Revit.</summary>
        static object Health() => new Dictionary<string, object>
        {
            ["app"] = "T3LabRevit",
            ["version"] = Entry.Version,
            ["pid"] = Process.GetCurrentProcess().Id,
            ["port"] = Entry.Port,
            ["revit"] = Entry.RevitVersion,
            ["active_document"] = Entry.ActiveDocument,
            ["calls"] = Interlocked.Read(ref Entry.CallCount),
            ["queue"] = QueueStatus(),
        };

        /// <summary>Conductor status with display names ("Claude Code"), never the bridge session ids.</summary>
        static Dictionary<string, object> QueueStatus()
        {
            Dictionary<string, object> q = Entry.Queue.Status();
            if (q.TryGetValue("agent", out object a)) q["agent"] = AgentCursors.Label(a as string);
            if (q.TryGetValue("waiting_agents", out object w) && w is List<object> list)
                q["waiting_agents"] = list.Select(x => (object)AgentCursors.Label(x as string)).Distinct().ToList();
            return q;
        }

        /// <summary>No deadline from the bridge (older bridge, direct client): a call waits at most this long for its turn.</summary>
        const int DefaultQueueWaitMs = 60000, MaxQueueWaitMs = 30 * 60 * 1000;

        static object CallTool(string name, Args args, string client, string lane, double queueWaitS)
        {
            ToolDef def = Registry.Get(name);
            if (def == null) return ErrorResult($"Unknown tool '{name}'. Call tools/list (or get_revit_context) to see the tools.");
            Interlocked.Increment(ref Entry.CallCount);
            var sw = Stopwatch.StartNew();
            string outcome = "ok";
            AgentCursors.CurrentAgent = client;
            AgentStep step = null;
            try { step = AgentCursors.Begin(def, args, client); }
            catch (Exception ex) { Log.Error("agent cursors: " + ex.Message); }
            bool ok = false, refused = false, busy = false;
            string error = null;
            object value = null;
            string pinned = args.Take(Registry.DocumentPin);   // another open model; resolved on Revit's thread
            int deadlineMs = queueWaitS > 0 ? (int)Math.Min(queueWaitS * 1000, MaxQueueWaitMs) : DefaultQueueWaitMs;
            try
            {
                if (AgentCursors.Blocks(def, args))
                {
                    refused = true;
                    throw new ToolError(AgentCursors.PausedMessage);
                }
                if (def.Background)
                {
                    value = def.Handler(null, args);
                }
                else
                {
                    // one call at a time, in a fair order; a call that cannot start in time never runs (Conductor)
                    using (Conductor.Ticket ticket = Entry.Queue.Enter(client, name, lane, deadlineMs,
                               (ahead, running) => AgentCursors.Queued(step, ahead, running)))
                    {
                        AgentCursors.Granted(step);
                        MainThread main = Entry.Main ?? throw new ToolError("The T3Lab Revit MCP server is stopping. Nothing was run; retry in a moment.");
                        // Revit must start it within what is left of the deadline, or it never runs (no late "ghost write")
                        int startMs = Math.Max(5000, deadlineMs - (int)sw.ElapsedMilliseconds);
                        value = main.Run(app =>
                        {
                            ticket.MarkRunning();
                            AgentCursors.OnMain(step);
                            // Pause pressed while this call waited for Revit
                            if (AgentCursors.Blocks(def, args))
                            {
                                refused = true;
                                throw new ToolError(AgentCursors.PausedMessage);
                            }
                            try { return Execute(def, args, app, pinned, step); }
                            finally { AgentCursors.EndCapture(step); }
                        }, startMs);
                    }
                }
                ok = true;
                return Success(value);
            }
            catch (Conductor.BusyException be)
            {
                busy = true;
                outcome = "busy: " + be.Message;
                error = "Not run, Revit was busy: " + be.Message;
                return BusyResult(be, deadlineMs / 1000.0);
            }
            catch (MainThread.NotStartedException ns)
            {
                busy = true;
                outcome = "not started: " + ns.Message;
                error = ns.Message;
                return Coded("revit_busy", ns.Message, true,
                    "Ask the user to finish or cancel what is open in Revit (a dialog, an edit mode), then retry.", 10);
            }
            catch (NeedsActiveException na)
            {
                outcome = "needs active document";
                error = na.Message;
                return Coded("needs_active_document", na.Message, true,
                    "Call again without revit_document, after manage_document action=activate makes that model the active one.");
            }
            catch (ToolError te) when (refused)
            {
                outcome = "paused";
                error = te.Message;
                return Coded("paused_by_user", te.Message, true,
                    "Do read-only work meanwhile, or ask the user to press Resume on the T3Lab Agent Cursors bar.");
            }
            catch (ToolError te)
            {
                outcome = "error: " + te.Message;
                error = te.Message;
                return ErrorResult(te.Message);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException re)
            {
                outcome = "revit error: " + re.GetType().Name;
                error = $"Revit: {re.Message}";
                return ErrorResult(error);
            }
            catch (Exception ex)
            {
                outcome = "exception: " + ex.GetType().Name;
                Log.Error(name + ": " + ex);
                error = $"{ex.GetType().Name}: {ex.Message}";
                return ErrorResult(error);
            }
            finally
            {
                Entry.LastCall = $"{name} ({sw.ElapsedMilliseconds} ms)";
                Log.Info($"{name} {sw.ElapsedMilliseconds}ms {outcome}");
                try { AgentCursors.End(step, ok, refused, busy, error, value); }
                catch (Exception ex) { Log.Error("agent cursors: " + ex.Message); }
            }
        }

        /// <summary>A call that never got its turn: what holds Revit, and when to try again. Nothing ran.</summary>
        static object BusyResult(Conductor.BusyException be, double waitedS)
        {
            string who = be.Agent == null ? null : AgentCursors.Label(be.Agent);
            string message = be.Agent == null
                ? $"Revit did not get to this call within {waitedS:0} s. Nothing was run."
                : be.Started
                    ? $"Revit is busy with another AI call ({be.Tool} for {who}, {be.RunningS:0} s so far). Nothing was run."
                    : $"Revit has not started the call ahead of this one ({be.Tool} for {who}): a dialog, an edit mode or a long " +
                      "operation may be blocking Revit. Nothing was run.";
            var d = new Dictionary<string, object>
            {
                ["error_code"] = "revit_busy",
                ["message"] = message,
                ["retryable"] = true,
                ["nothing_ran"] = true,
                ["retry_after_s"] = Math.Max(5, Math.Min(30, 5 + be.Ahead * 5)),
                ["ahead"] = be.Ahead,
                ["hint"] = be.Started || be.Agent == null
                    ? "Do work that does not need Revit meanwhile (plan, read results you have), then retry after retry_after_s."
                    : "Ask the user to finish or cancel what is open in Revit, then retry.",
            };
            if (be.Agent != null)
                d["running"] = new Dictionary<string, object>
                {
                    ["agent"] = who, ["tool"] = be.Tool, ["running_s"] = be.RunningS, ["state"] = be.Started ? "running" : "waiting_for_revit",
                };
            return ErrorResult(Json.Write(d));
        }

        /// <summary>A refusal an agent can act on: {error_code, message, retryable, nothing_ran, hint}.</summary>
        static object Coded(string code, string message, bool retryable, string hint, int retryAfterS = 0)
        {
            var d = new Dictionary<string, object>
            {
                ["error_code"] = code,
                ["message"] = message,
                ["retryable"] = retryable,
                ["nothing_ran"] = true,
                ["hint"] = hint,
            };
            if (retryAfterS > 0) d["retry_after_s"] = retryAfterS;
            return ErrorResult(Json.Write(d));
        }

        static object Execute(ToolDef def, Args args, UIApplication app, string pinned, AgentStep step)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (pinned != null)
            {
                // another open model, or one opened in the background: the call works there and the user's view stays
                Document target = Documents.Find(app, pinned);
                if (doc == null || !target.Equals(doc))
                {
                    if (!Registry.TakesPin(def))
                        throw new NeedsActiveException($"{def.Name} works in the active model only, and '{target.Title}' is not active in Revit.");
                    if (target.IsLinked) throw new ToolError($"'{target.Title}' is loaded as a link: open it as a model to work in it.");
                    doc = target;
                    uidoc = null;
                }
            }
            AgentCursors.SetDocument(step, doc?.Title, uidoc != null);
            AgentCursors.StartCapture(step, def, doc);   // after the pin: "before" boxes come from the model the call works in
            if (def.NeedsDocument && doc == null)
                throw new ToolError("No model is open in Revit. Ask the user to open one, or use manage_document action=open.");

            var ctx = new ToolContext { App = app, UiDoc = uidoc, Doc = doc };
            object result;
            if (def.Kind == ToolKind.Write && def.Tx && doc != null)
            {
                if (doc.IsReadOnly) throw new ToolError($"'{doc.Title}' is read-only in Revit; nothing was changed.");
                result = InTransaction(ctx, def.Title, () => def.Handler(ctx, args));
            }
            else
            {
                result = def.Handler(ctx, args);
            }

            if (ctx.Warnings.Count > 0 && result is Dictionary<string, object> d && !d.ContainsKey("revit_warnings"))
                d["revit_warnings"] = ctx.Warnings.Distinct().Take(20).ToList();
            // say where it happened when that is not the model the user looks at
            if (uidoc == null && doc != null && result is Dictionary<string, object> r && !r.ContainsKey("document"))
            {
                r["document"] = doc.Title;
                r["active_document"] = false;
            }
            return result;
        }

        /// <summary>One Undo step named "T3Lab MCP · title": warnings are dismissed (and reported), errors roll it back.</summary>
        internal static T InTransaction<T>(ToolContext ctx, string title, Func<T> body)
        {
            Document doc = ctx.Doc;
            using (var t = new Transaction(doc, "T3Lab MCP · " + title))
            {
                var collector = new FailureCollector(ctx.Warnings);
                FailureHandlingOptions opts = t.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(collector);
                opts.SetClearAfterRollback(true);
                t.SetFailureHandlingOptions(opts);
                if (t.Start() != TransactionStatus.Started)
                    throw new ToolError("Revit could not start a transaction (another edit is open). Nothing was changed.");
                T result;
                try { result = body(); }
                catch
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    throw;
                }
                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                {
                    string why = collector.Errors.Count > 0 ? string.Join("; ", collector.Errors.Distinct().Take(5)) : status.ToString();
                    throw new ToolError("Revit rolled the change back, nothing was changed: " + why);
                }
                return result;
            }
        }

        sealed class FailureCollector : IFailuresPreprocessor
        {
            readonly List<string> _warnings;
            public readonly List<string> Errors = new List<string>();

            public FailureCollector(List<string> warnings)
            {
                _warnings = warnings;
            }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                bool error = false;
                foreach (FailureMessageAccessor m in fa.GetFailureMessages())
                {
                    string text = m.GetDescriptionText();
                    if (m.GetSeverity() == FailureSeverity.Warning)
                    {
                        _warnings.Add(text);
                        fa.DeleteWarning(m);
                    }
                    else
                    {
                        Errors.Add(text);
                        error = true;
                    }
                }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }

        static object Success(object value)
        {
            if (value is ImageResult img)
            {
                return new Dictionary<string, object>
                {
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object> { ["type"] = "image", ["data"] = img.Base64, ["mimeType"] = img.MimeType },
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = Json.Write(img.Info) },
                    },
                };
            }
            return new Dictionary<string, object>
            {
                ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = Json.Write(value) } },
            };
        }

        static object ErrorResult(string message) => new Dictionary<string, object>
        {
            ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = message } },
            ["isError"] = true,
        };
    }
}
