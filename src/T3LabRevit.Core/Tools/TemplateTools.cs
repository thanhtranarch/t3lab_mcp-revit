using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    /// <summary>View templates, view filters, view settings and project parameters.</summary>
    internal static class TemplateTools
    {
        public static void Register()
        {
            Registry.Add("manage_view_templates", "View templates", ToolKind.Write,
                "action=list (templates and how many views use each), apply (template + views: assigns it), apply_once (copies its " +
                "settings, no link), remove (views: no template), create_from_view (view + name), transfer (from_document: title " +
                "of another open model, templates: names or * ; copies them into the active model).",
                ObjR("action", P("action", Enum("What to do", "list", "apply", "apply_once", "remove", "create_from_view", "transfer")),
                    P("template", Str("Template name or id")), P("views", Strs("View names or ids")), P("view", Str("create_from_view: source view")),
                    P("name", Str("create_from_view: template name")), P("from_document", Str("transfer: source model title")),
                    P("templates", Strs("transfer: template names, or *"))),
                ManageTemplates);

            Registry.Add("list_view_filters", "List view filters", ToolKind.Read,
                "Rule-based filters of the model (name, categories) and, with view, the filters applied to it with visibility.",
                Obj(P("view", Str("Also list the filters on this view (name or id)"))), ListFilters);

            Registry.Add("create_view_filter", "Create a view filter", ToolKind.Write,
                "Create a rule-based view filter: categories + rules [{parameter, op: equals|not_equals|contains|not_contains|begins_with|" +
                "greater|less, value}] (all must match; numbers in mm / m2 / m3 / degrees for those parameters). Optional: add it to views " +
                "with an override colour rgb and / or visible=false.",
                ObjR("name,categories,rules", P("name", Str("Filter name")), P("categories", Strs("Categories")),
                    P("rules", "{'type':'array','items':{'type':'object','properties':{'parameter':{'type':'string'},'op':{'type':'string'},'value':{}}},'description':'Rules, all must match'}"),
                    P("views", Strs("Views (names or ids) to add the filter to")),
                    P("rgb", "{'type':'array','items':{'type':'integer'},'description':'Override colour [r, g, b] in those views'}"),
                    P("visible", Bool("false hides the filtered elements in those views"))),
                CreateFilter);

            Registry.Add("manage_view", "View settings", ToolKind.Write,
                "Change a view (default active): scale, detail_level (coarse|medium|fine), display (wireframe|hidden|shaded|realistic|" +
                "consistent), discipline, crop (true / false), crop_box {min, max} mm, section_box {min, max} mm (3D views), " +
                "section_box_active, name.",
                Obj(P("view", Str("View name or id (default active)")), P("name", Str("New name")), P("scale", Int("e.g. 100 for 1:100")),
                    P("detail_level", Enum("Detail level", "coarse", "medium", "fine")),
                    P("display", Enum("Visual style", "wireframe", "hidden", "shaded", "realistic", "consistent")),
                    P("discipline", Enum("Discipline", "architectural", "structural", "mechanical", "electrical", "plumbing", "coordination")),
                    P("crop", Bool("Crop region on")), P("crop_box", ObjAny("{min:[x,y,z], max:[x,y,z]} mm, model coordinates")),
                    P("section_box", ObjAny("{min:[x,y,z], max:[x,y,z]} mm (3D view)")), P("section_box_active", Bool("3D: section box on"))),
                ManageView);

            Registry.Add("list_project_parameters", "List project parameters", ToolKind.Read,
                "Project and shared parameters bound in the model: name, type, instance or type, categories, group, shared GUID.",
                Obj(), ListProjectParameters);

            Registry.Add("create_project_parameter", "Add a project parameter", ToolKind.Write,
                "Add a parameter to categories: name, data_type (text|integer|number|length|area|volume|angle|yesno|url|material), " +
                "instance (default true), group (data|text|identity|dimensions|constraints|general), categories. Made through a " +
                "temporary shared parameter file, so it can be scheduled and tagged.",
                ObjR("name,categories", P("name", Str("Parameter name")),
                    P("data_type", Enum("Data type", "text", "integer", "number", "length", "area", "volume", "angle", "yesno", "url", "material")),
                    P("instance", Bool("Instance (true) or type (false)")),
                    P("group", Enum("Properties group", "data", "text", "identity", "dimensions", "constraints", "general")),
                    P("categories", Strs("Categories"))),
                CreateProjectParameter);
        }

        // ---------------------------------------------------------------- templates

        static View Template(Document doc, string key)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
            View t = (long.TryParse(key, out long v) ? all.FirstOrDefault(x => Id.V(x.Id) == v) : null)
                     ?? all.FirstOrDefault(x => string.Equals(x.Name, key?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (t == null) throw new ToolError($"No view template '{key}'. Templates: " + string.Join(", ", all.Select(x => x.Name).Take(40)));
            return t;
        }

        sealed class UseDestination : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
        }

        internal static Document OpenDocument(Autodesk.Revit.UI.UIApplication app, string title)
        {
            foreach (Document d in app.Application.Documents)
                if (!d.IsLinked && (string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase) || string.Equals(d.PathName, title, StringComparison.OrdinalIgnoreCase)))
                    return d;
            throw new ToolError($"No open model '{title}' (get_revit_context lists them).");
        }

        static object ManageTemplates(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string action = a.ReqStr("action").ToLowerInvariant();
            switch (action)
            {
                case "list":
                {
                    var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
                    var rows = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).OrderBy(v => v.Name)
                        .Select(t => (object)new Dictionary<string, object>
                        {
                            ["id"] = Id.V(t.Id), ["name"] = t.Name, ["type"] = t.ViewType.ToString(),
                            ["used_by"] = views.Count(v => Id.V(v.ViewTemplateId) == Id.V(t.Id)),
                        }).ToList();
                    return new Dictionary<string, object> { ["templates"] = rows };
                }
                case "apply":
                case "apply_once":
                {
                    View t = Template(doc, a.ReqStr("template"));
                    int n = 0;
                    var failed = new List<object>();
                    foreach (string key in a.Strs("views"))
                    {
                        View v = Rv.View(doc, key, null);
                        try
                        {
                            if (action == "apply") v.ViewTemplateId = t.Id;
                            else v.ApplyViewTemplateParameters(t);
                            n++;
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { failed.Add(new Dictionary<string, object> { ["view"] = v.Name, ["error"] = ex.Message }); }
                    }
                    return new Dictionary<string, object> { ["template"] = t.Name, ["views"] = n, ["failed"] = failed };
                }
                case "remove":
                {
                    int n = 0;
                    foreach (string key in a.Strs("views")) { Rv.View(doc, key, null).ViewTemplateId = ElementId.InvalidElementId; n++; }
                    return new Dictionary<string, object> { ["views"] = n };
                }
                case "create_from_view":
                {
                    View src = Rv.View(doc, a.Str("view"), c.UiDoc.ActiveView);
                    View t = src.CreateViewTemplate();
                    if (a.Has("name")) t.Name = a.Str("name");
                    return new Dictionary<string, object> { ["id"] = Id.V(t.Id), ["name"] = t.Name, ["element_ids"] = new List<object> { Id.V(t.Id) } };
                }
                case "transfer":
                {
                    Document src = OpenDocument(c.App, a.ReqStr("from_document"));
                    if (src.Equals(doc)) throw new ToolError("from_document is the active model.");
                    List<string> names = a.Strs("templates");
                    var srcTemplates = new FilteredElementCollector(src).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate)
                        .Where(v => names.Contains("*") || names.Any(n => string.Equals(n, v.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (srcTemplates.Count == 0) throw new ToolError("No matching templates in " + src.Title);
                    var opts = new CopyPasteOptions();
                    opts.SetDuplicateTypeNamesHandler(new UseDestination());
                    ICollection<ElementId> made = ElementTransformUtils.CopyElements(src, srcTemplates.Select(v => v.Id).ToList(), doc, Transform.Identity, opts);
                    return new Dictionary<string, object> { ["copied"] = made.Count, ["names"] = made.Select(doc.GetElement).Where(e => e != null).Select(e => (object)e.Name).ToList() };
                }
            }
            throw new ToolError("Unknown action " + action);
        }

        // ---------------------------------------------------------------- filters

        static object ListFilters(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            var rows = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().OrderBy(f => f.Name)
                .Select(f => (object)new Dictionary<string, object>
                {
                    ["id"] = Id.V(f.Id), ["name"] = f.Name,
                    ["categories"] = f.GetCategories().Select(id => (object)Category.GetCategory(doc, id)?.Name).ToList(),
                }).ToList();
            var result = new Dictionary<string, object> { ["filters"] = rows };
            if (a.Has("view"))
            {
                View v = Rv.View(doc, a.Str("view"), c.UiDoc.ActiveView);
                result["on_view"] = v.GetFilters().Select(id => (object)new Dictionary<string, object>
                {
                    ["id"] = Id.V(id), ["name"] = doc.GetElement(id)?.Name, ["visible"] = v.GetFilterVisibility(id),
                }).ToList();
            }
            return result;
        }

        static ElementId FilterParameter(Document doc, ICollection<ElementId> cats, string name)
        {
            foreach (ElementId pid in ParameterFilterUtilities.GetFilterableParametersInCommon(doc, cats))
            {
                string n;
                long v = Id.V(pid);
                if (v < 0)
                {
                    try { n = LabelUtils.GetLabelFor((BuiltInParameter)(int)v); } catch (Exception) { continue; }
                    if (string.Equals(((BuiltInParameter)(int)v).ToString(), name, StringComparison.OrdinalIgnoreCase)) return pid;
                }
                else n = doc.GetElement(pid)?.Name;
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return pid;
            }
            throw new ToolError($"'{name}' is not a filterable parameter common to all these categories.");
        }

        static FilterRule Rule(Document doc, ElementId pid, string op, object value, ICollection<ElementId> cats)
        {
            op = (op ?? "equals").ToLowerInvariant();
            string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            bool numeric = value is int || value is long || value is double || value is decimal;
            if (numeric)
            {
                double d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                double raw = ToInternal(doc, pid, cats, d);
                double eps = Math.Max(1e-6, Math.Abs(raw) * 1e-6);
                switch (op)
                {
                    case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, raw, eps);
                    case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, raw, eps);
                    case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, raw, eps);
                    case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, raw, eps);
                }
            }
            switch (op)
            {
#if REVIT2023_OR_GREATER
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, text);
                case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, text);
                case "contains": return ParameterFilterRuleFactory.CreateContainsRule(pid, text);
                case "not_contains": return ParameterFilterRuleFactory.CreateNotContainsRule(pid, text);
                case "begins_with": return ParameterFilterRuleFactory.CreateBeginsWithRule(pid, text);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, text);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, text);
#else
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, text, false);
                case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, text, false);
                case "contains": return ParameterFilterRuleFactory.CreateContainsRule(pid, text, false);
                case "not_contains": return ParameterFilterRuleFactory.CreateNotContainsRule(pid, text, false);
                case "begins_with": return ParameterFilterRuleFactory.CreateBeginsWithRule(pid, text, false);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, text, false);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, text, false);
#endif
            }
            throw new ToolError($"Unknown op '{op}'.");
        }

        /// <summary>A number in tool units to Revit units, from the spec of that parameter on some element of these categories.</summary>
        static double ToInternal(Document doc, ElementId pid, ICollection<ElementId> cats, double value)
        {
            foreach (ElementId cat in cats)
            {
                Element e = new FilteredElementCollector(doc).OfCategoryId(cat).WhereElementIsNotElementType().FirstElement();
                if (e == null) continue;
                Parameter p = Id.V(pid) < 0 ? e.get_Parameter((BuiltInParameter)(int)Id.V(pid)) : e.Parameters.Cast<Parameter>().FirstOrDefault(x => Id.V(x.Id) == Id.V(pid));
                if (p != null && p.StorageType == StorageType.Double) return Rv.FromToolUnits(p, value);
                if (p != null) return value;
            }
            return value;
        }

        static object CreateFilter(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            List<ElementId> cats = a.Strs("categories").Select(n => Rv.Category(doc, n).Id).ToList();
            var rules = new List<FilterRule>();
            foreach (Args r in a.Objs("rules"))
                rules.Add(Rule(doc, FilterParameter(doc, cats, r.ReqStr("parameter")), r.Str("op"), r.Raw("value"), cats));
            if (rules.Count == 0) throw new ToolError("Give at least one rule.");
            ElementFilter filter = rules.Count == 1
                ? (ElementFilter)new ElementParameterFilter(rules[0])
                : new LogicalAndFilter(rules.Select(r => (ElementFilter)new ElementParameterFilter(r)).ToList());
            ParameterFilterElement pfe = ParameterFilterElement.Create(doc, a.ReqStr("name"), cats, filter);
            var views = new List<object>();
            foreach (string key in a.Strs("views"))
            {
                View v = Rv.View(doc, key, null);
                if (Id.Valid(v.ViewTemplateId)) { views.Add(new Dictionary<string, object> { ["view"] = v.Name, ["skipped"] = "controlled by a view template" }); continue; }
                v.AddFilter(pfe.Id);
                List<string> rgb = a.Strs("rgb");
                if (rgb.Count == 3)
                {
                    var col = new Color((byte)Args.ToDouble(rgb[0], "rgb"), (byte)Args.ToDouble(rgb[1], "rgb"), (byte)Args.ToDouble(rgb[2], "rgb"));
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(col);
                    ElementId solid = ViewTools.SolidFill(doc);
                    if (Id.Valid(solid)) { ogs.SetSurfaceForegroundPatternId(solid); ogs.SetSurfaceForegroundPatternColor(col); }
                    v.SetFilterOverrides(pfe.Id, ogs);
                }
                if (a.Has("visible")) v.SetFilterVisibility(pfe.Id, a.Bool("visible", true));
                views.Add(new Dictionary<string, object> { ["view"] = v.Name, ["added"] = true });
            }
            return new Dictionary<string, object> { ["filter_id"] = Id.V(pfe.Id), ["name"] = pfe.Name, ["views"] = views };
        }

        // ---------------------------------------------------------------- view settings

        static BoundingBoxXYZ BoxArg(Args box)
        {
            XYZ min = box.ReqPt("min"), max = box.ReqPt("max");
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
                Max = new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)),
            };
        }

        static object ManageView(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            View v = Rv.View(doc, a.Str("view"), c.UiDoc.ActiveView);
            var changed = new List<string>();
            if (a.Has("name")) { v.Name = a.Str("name"); changed.Add("name"); }
            if (a.Has("scale")) { v.Scale = a.Int("scale", 100); changed.Add("scale"); }
            if (a.Has("detail_level"))
            {
                string d = a.Str("detail_level").ToLowerInvariant();
                v.DetailLevel = d == "coarse" ? ViewDetailLevel.Coarse : d == "fine" ? ViewDetailLevel.Fine : ViewDetailLevel.Medium;
                changed.Add("detail_level");
            }
            if (a.Has("display"))
            {
                string d = a.Str("display").ToLowerInvariant();
                v.DisplayStyle = d == "wireframe" ? DisplayStyle.Wireframe : d == "hidden" ? DisplayStyle.HLR : d == "realistic" ? DisplayStyle.Realistic
                    : d == "consistent" ? DisplayStyle.FlatColors : DisplayStyle.ShadingWithEdges;
                changed.Add("display");
            }
            if (a.Has("discipline"))
            {
                string d = a.Str("discipline").ToLowerInvariant();
                v.Discipline = d == "structural" ? ViewDiscipline.Structural : d == "mechanical" ? ViewDiscipline.Mechanical
                    : d == "electrical" ? ViewDiscipline.Electrical : d == "plumbing" ? ViewDiscipline.Plumbing
                    : d == "coordination" ? ViewDiscipline.Coordination : ViewDiscipline.Architectural;
                changed.Add("discipline");
            }
            if (a.Has("crop")) { v.CropBoxActive = a.Bool("crop", true); changed.Add("crop"); }
            if (a.Has("crop_box"))
            {
                BoundingBoxXYZ cur = v.CropBox;
                BoundingBoxXYZ want = BoxArg(a.Obj("crop_box"));
                Transform inv = cur.Transform.Inverse;
                XYZ p0 = inv.OfPoint(want.Min), p1 = inv.OfPoint(want.Max);
                cur.Min = new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), cur.Min.Z);
                cur.Max = new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), cur.Max.Z);
                v.CropBoxActive = true;
                v.CropBox = cur;
                changed.Add("crop_box");
            }
            if (a.Has("section_box") || a.Has("section_box_active"))
            {
                if (!(v is View3D v3)) throw new ToolError("A section box needs a 3D view.");
                if (a.Has("section_box")) { v3.SetSectionBox(BoxArg(a.Obj("section_box"))); changed.Add("section_box"); }
                if (a.Has("section_box_active")) { v3.IsSectionBoxActive = a.Bool("section_box_active", true); changed.Add("section_box_active"); }
            }
            if (changed.Count == 0) throw new ToolError("Nothing to change.");
            return new Dictionary<string, object> { ["view"] = v.Name, ["changed"] = changed };
        }

        // ---------------------------------------------------------------- project parameters

        static object ListProjectParameters(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            var rows = new List<object>();
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                Definition def = it.Key;
                var binding = it.Current as ElementBinding;
                var d = new Dictionary<string, object>
                {
                    ["name"] = def.Name,
                    ["instance"] = binding is InstanceBinding,
                    ["categories"] = binding == null ? new List<object>() : binding.Categories.Cast<Category>().Select(x => (object)x.Name).ToList(),
                };
                try { d["data_type"] = def.GetDataType().TypeId; } catch (Exception) { }
                try { d["group"] = def.GetGroupTypeId().TypeId; } catch (Exception) { }
                if (def is InternalDefinition idf && doc.GetElement(idf.Id) is SharedParameterElement sp) d["guid"] = sp.GuidValue.ToString();
                rows.Add(d);
            }
            return new Dictionary<string, object> { ["parameters"] = rows.OrderBy(r => (string)((Dictionary<string, object>)r)["name"]).ToList() };
        }

        static ForgeTypeId SpecOf(string t)
        {
            switch ((t ?? "text").ToLowerInvariant())
            {
                case "integer": return SpecTypeId.Int.Integer;
                case "number": return SpecTypeId.Number;
                case "length": return SpecTypeId.Length;
                case "area": return SpecTypeId.Area;
                case "volume": return SpecTypeId.Volume;
                case "angle": return SpecTypeId.Angle;
                case "yesno": return SpecTypeId.Boolean.YesNo;
                case "url": return SpecTypeId.String.Url;
                case "material": return SpecTypeId.Reference.Material;
                default: return SpecTypeId.String.Text;
            }
        }

        static ForgeTypeId GroupOf(string g)
        {
            switch ((g ?? "data").ToLowerInvariant())
            {
                case "text": return GroupTypeId.Text;
                case "identity": return GroupTypeId.IdentityData;
                case "dimensions": return GroupTypeId.Geometry;
                case "constraints": return GroupTypeId.Constraints;
                case "general": return GroupTypeId.General;
                default: return GroupTypeId.Data;
            }
        }

        static object CreateProjectParameter(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            Autodesk.Revit.ApplicationServices.Application app = c.App.Application;
            string name = a.ReqStr("name");
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (string.Equals(it.Key.Name, name, StringComparison.OrdinalIgnoreCase)) throw new ToolError($"The model already has a parameter '{name}'.");

            var cats = app.Create.NewCategorySet();
            foreach (string n in a.Strs("categories"))
            {
                Category cat = Rv.Category(doc, n);
                if (!cat.AllowsBoundParameters) throw new ToolError($"Category '{cat.Name}' does not take parameters.");
                cats.Insert(cat);
            }
            string previous = app.SharedParametersFilename;
            string temp = Path.Combine(Path.GetTempPath(), "T3LabRevitMCP_shared_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            File.WriteAllText(temp, "");
            try
            {
                app.SharedParametersFilename = temp;
                DefinitionFile file = app.OpenSharedParameterFile();
                DefinitionGroup group = file.Groups.Create("T3Lab MCP");
                var opts = new ExternalDefinitionCreationOptions(name, SpecOf(a.Str("data_type")));
                Definition def = group.Definitions.Create(opts);
                Binding binding = a.Bool("instance", true) ? (Binding)app.Create.NewInstanceBinding(cats) : app.Create.NewTypeBinding(cats);
                if (!doc.ParameterBindings.Insert(def, binding, GroupOf(a.Str("group"))))
                    throw new ToolError("Revit did not add the parameter.");
                return new Dictionary<string, object>
                {
                    ["name"] = name, ["data_type"] = a.Str("data_type") ?? "text", ["instance"] = a.Bool("instance", true),
                    ["categories"] = a.Strs("categories"), ["guid"] = (def as ExternalDefinition)?.GUID.ToString(),
                };
            }
            finally
            {
                try { app.SharedParametersFilename = previous ?? ""; } catch (Exception) { }
                try { File.Delete(temp); } catch (Exception) { }
            }
        }
    }
}
