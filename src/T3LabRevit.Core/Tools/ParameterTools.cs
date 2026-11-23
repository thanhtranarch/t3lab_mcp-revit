using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    internal static class ParameterTools
    {
        public static void Register()
        {
            Registry.Add("set_parameter", "Set parameter values", ToolKind.Write,
                "Set parameters on elements, one Undo step for the whole call. Either element_ids + values {name: value} (same values on " +
                "every element), or changes [{element_id, parameter, value}] (a value per element). target=type writes the type " +
                "parameter instead (every instance of that type changes). Numbers are mm, m2, m3, degrees; text on a number parameter " +
                "is parsed by Revit (\"1200\", \"1.2 m\"); yes/no as true/false; an id parameter takes an id or a level / material / " +
                "type name. Built-in names like ALL_MODEL_MARK work too. Elements that fail are listed; the others are still set.",
                Obj(Ids,
                    P("values", ObjAny("Parameter name -> value, applied to every element in element_ids")),
                    P("changes", "{'type':'array','items':{'type':'object','properties':{'element_id':{'type':'integer'},'parameter':{'type':'string'},'value':{}}},'description':'One change per item'}"),
                    P("target", Enum("instance (default) or type", "instance", "type"))),
                Set);
        }

        static object Set(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            bool onType = string.Equals(a.Str("target"), "type", StringComparison.OrdinalIgnoreCase);
            var jobs = new List<Tuple<ElementId, string, object>>();
            if (a.Has("values"))
            {
                Args values = a.Obj("values");
                List<ElementId> ids = a.Ids("element_ids");
                if (ids.Count == 0) throw new ToolError("values needs element_ids.");
                foreach (ElementId id in ids)
                    foreach (string key in values.Keys)
                        jobs.Add(Tuple.Create(id, key, values.Raw(key)));
            }
            foreach (Args ch in a.Objs("changes"))
                jobs.Add(Tuple.Create(Id.Make(Args.ToLong(ch.Raw("element_id"), "element_id")), ch.ReqStr("parameter"), ch.Raw("value")));
            if (jobs.Count == 0) throw new ToolError("Nothing to set: give element_ids + values, or changes.");

            int ok = 0;
            var failed = new List<object>();
            var doneTypes = new HashSet<string>();
            foreach (var job in jobs)
            {
                Element e = doc.GetElement(job.Item1);
                try
                {
                    if (e == null) throw new ToolError("no such element");
                    Element target = e;
                    if (onType)
                    {
                        target = e as ElementType ?? Rv.TypeElement(e) ?? throw new ToolError("element has no type");
                        if (!doneTypes.Add(Id.V(target.Id) + "|" + job.Item2)) { ok++; continue; }
                    }
                    Parameter p = Rv.Param(target, job.Item2);
                    if (p == null)
                    {
                        string hint = !onType && Rv.TypeElement(e) is ElementType t && Rv.Param(t, job.Item2) != null ? " (it is a type parameter: use target=type)" : "";
                        throw new ToolError($"no parameter '{job.Item2}'{hint}");
                    }
                    Rv.SetParam(p, job.Item3);
                    ok++;
                }
                catch (Exception ex) when (ex is ToolError || ex is Autodesk.Revit.Exceptions.ApplicationException)
                {
                    failed.Add(new Dictionary<string, object> { ["element_id"] = Id.V(job.Item1), ["parameter"] = job.Item2, ["error"] = ex.Message });
                }
            }
            if (ok == 0) throw new ToolError("Nothing was set: " + Json.Write(failed.Take(10).ToList()));
            return new Dictionary<string, object> { ["set"] = ok, ["failed"] = failed.Take(50).ToList(), ["failed_count"] = failed.Count };
        }
    }
}
