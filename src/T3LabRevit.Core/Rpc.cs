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
                    case "tools/call": result = CallTool(p.ReqStr("name"), new Args(p.Raw("arguments")), AgentCursors.ClientName(p.Raw("client"))); break;
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
        };

        static object CallTool(string name, Args args, string client)
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
            bool ok = false, refused = false;
            string error = null;
            object value = null;
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
                    value = Entry.Main.Run(app =>
                    {
                        // Pause pressed while this call waited for Revit
                        if (AgentCursors.Blocks(def, args))
                        {
                            refused = true;
                            throw new ToolError(AgentCursors.PausedMessage);
                        }
                        AgentCursors.StartCapture(step, def, app);
                        try { return Execute(def, args, app); }
                        finally { AgentCursors.EndCapture(step); }
                    });
                }
                ok = true;
                return Success(value);
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
                try { AgentCursors.End(step, ok, refused, error, value); }
                catch (Exception ex) { Log.Error("agent cursors: " + ex.Message); }
            }
        }

        static object Execute(ToolDef def, Args args, UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc?.Document;
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
