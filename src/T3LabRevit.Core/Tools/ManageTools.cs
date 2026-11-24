using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    /// <summary>Worksets, links, CAD, groups, materials, revisions: list (read) + manage (one Undo step each).</summary>
    internal static class ManageTools
    {
        public static void Register()
        {
            Registry.Add("list_worksets", "List worksets", ToolKind.Read,
                "User worksets of a workshared model: id, name, open, editable, owner, visible by default, active, element count.",
                Obj(P("count", Bool("Count elements per workset (slower on big models)"))), ListWorksets);

            Registry.Add("manage_worksets", "Create, rename or activate worksets", ToolKind.Write,
                "action=create (names), rename (name, new_name), set_active (name), visibility (name, visible true/false in a view, " +
                "default the active one), delete (name, move_to: workset that receives its elements).",
                ObjR("action", P("action", Enum("What to do", "create", "rename", "set_active", "visibility", "delete")),
                    P("names", Strs("create: workset names")), P("name", Str("Workset name")), P("new_name", Str("rename: new name")),
                    P("visible", Bool("visibility: show (true) or hide (false)")), P("view", Str("visibility: view name or id")),
                    P("move_to", Str("delete: workset that receives the elements"))),
                ManageWorksets);

            Registry.Add("set_element_workset", "Move elements to a workset", ToolKind.Write,
                "Put elements on a workset (by name). Elements must be editable by you (not borrowed by someone else).",
                ObjR("element_ids,workset", Ids, P("workset", Str("Workset name"))), SetElementWorkset);

            Registry.Add("list_links", "List linked models and CAD", ToolKind.Read,
                "Revit links (type, path, loaded, instances, pinned) and CAD imports / links (name, view-specific, owner view, pinned).",
                Obj(), ListLinks);

            Registry.Add("manage_links", "Load, unload, reload or link files", ToolKind.App,
                "action=link (path of a .rvt, or .dwg / .dxf / .dgn / .dwf: placed origin to origin, CAD in the active view only " +
                "when this_view_only=true), load, unload, reload (name of the link type), reload_from (name + path), pin / unpin " +
                "(element_ids of link instances).",
                ObjR("action", P("action", Enum("What to do", "link", "load", "unload", "reload", "reload_from", "pin", "unpin")),
                    P("path", Str("File path for link / reload_from")), P("name", Str("Link type name (from list_links)")),
                    P("this_view_only", Bool("CAD link: only in the active view")), Ids),
                ManageLinks, destructive: true);

            Registry.Add("manage_groups", "Group, ungroup and place groups", ToolKind.Write,
                "action=list (group types with instance counts), create (element_ids, name), ungroup (element_ids of groups), " +
                "place (group_type name, points mm), rename (group_type, new_name).",
                ObjR("action", P("action", Enum("What to do", "list", "create", "ungroup", "place", "rename")),
                    Ids, P("name", Str("create: group name")), P("group_type", Str("Group type name")),
                    P("points", Pts("place: points [x, y] or [x, y, z] mm")), P("new_name", Str("rename: new name"))),
                ManageGroups);

            Registry.Add("list_materials", "List materials", ToolKind.Read,
                "Materials of the model: id, name, class, colour [r,g,b], transparency, whether elements use it (used=true lists only those).",
                Obj(P("name_contains", Str("Part of the name")), P("used", Bool("Only materials painted or assigned on elements")), Limit), ListMaterials);

            Registry.Add("manage_material", "Create or change materials", ToolKind.Write,
                "action=create (name, from: material to duplicate), rename (name, new_name), set (name; rgb [r,g,b], transparency 0-100, " +
                "shininess 0-128, smoothness 0-100, material_class), delete (name).",
                ObjR("action,name", P("action", Enum("What to do", "create", "rename", "set", "delete")),
                    P("name", Str("Material name")), P("from", Str("create: material to copy")), P("new_name", Str("rename: new name")),
                    P("rgb", "{'type':'array','items':{'type':'integer'},'description':'Colour [r, g, b] 0-255'}"),
                    P("transparency", Int("0-100")), P("shininess", Int("0-128")), P("smoothness", Int("0-100")),
                    P("material_class", Str("Material class, e.g. Concrete"))),
                ManageMaterial, destructive: true);

            Registry.Add("get_material_quantities", "Material take-off", ToolKind.Read,
                "Area (m2) and volume (m3) per material over elements of some categories (default walls, floors, roofs, ceilings, " +
                "structural columns and framing), optionally per level.",
                Obj(P("categories", Strs("Categories")), P("level", Str("Only this level")), P("by_category", Bool("Split each material by category"))),
                MaterialQuantities);

            Registry.Add("manage_revisions", "Revisions", ToolKind.Write,
                "action=list (all revisions and the sheets showing them), create (description, date, issued_by, issued_to), " +
                "add_to_sheets (revision id or description + sheets), remove_from_sheets, issue (mark issued).",
                ObjR("action", P("action", Enum("What to do", "list", "create", "add_to_sheets", "remove_from_sheets", "issue")),
                    P("revision", Str("Revision id or description")), P("description", Str("create: description")),
                    P("date", Str("create: date text")), P("issued_by", Str("Issued by")), P("issued_to", Str("Issued to")),
                    P("sheets", Strs("Sheet numbers, or * for all"))),
                ManageRevisions);

            Registry.Add("project_storage", "Store notes in the model", ToolKind.Write,
                "Keep small key / value data inside the model (extensible storage on Project Information), e.g. a checklist or the " +
                "state of a long job: action=get (key, or all), set (key, value: any JSON), delete (key).",
                ObjR("action", P("action", Enum("What to do", "get", "set", "delete")), P("key", Str("Key")), P("value", Any("Value to store (JSON)"))),
                ProjectStorage);
        }

        // ---------------------------------------------------------------- worksets

        static void NeedWorkshared(Document doc)
        {
            if (!doc.IsWorkshared) throw new ToolError($"'{doc.Title}' is not workshared (no worksets).");
        }

        static Workset Workset(Document doc, string name)
        {
            NeedWorkshared(doc);
            var all = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets();
            Workset w = all.FirstOrDefault(x => string.Equals(x.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (w == null) throw new ToolError($"No workset '{name}'. Worksets: " + string.Join(", ", all.Select(x => x.Name)));
            return w;
        }

        static object ListWorksets(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            NeedWorkshared(doc);
            WorksetId active = doc.GetWorksetTable().GetActiveWorksetId();
            Dictionary<int, int> counts = null;
            if (a.Bool("count", false))
                counts = new FilteredElementCollector(doc).WhereElementIsNotElementType().GroupBy(e => e.WorksetId.IntegerValue).ToDictionary(g => g.Key, g => g.Count());
            var rows = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name).Select(w =>
            {
                var d = new Dictionary<string, object>
                {
                    ["id"] = w.Id.IntegerValue, ["name"] = w.Name, ["open"] = w.IsOpen, ["editable"] = w.IsEditable,
                    ["owner"] = w.Owner, ["visible_by_default"] = w.IsVisibleByDefault, ["active"] = w.Id == active,
                };
                if (counts != null) d["elements"] = counts.TryGetValue(w.Id.IntegerValue, out int n) ? n : 0;
                return (object)d;
            }).ToList();
            return new Dictionary<string, object> { ["worksets"] = rows };
        }

        static object ManageWorksets(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            NeedWorkshared(doc);
            string action = a.ReqStr("action").ToLowerInvariant();
            switch (action)
            {
                case "create":
                {
                    var made = new List<object>();
                    foreach (string n in a.Strs("names").Concat(a.Has("name") ? new[] { a.Str("name") } : new string[0]))
                    {
                        if (!WorksetTable.IsWorksetNameUnique(doc, n)) { made.Add(new Dictionary<string, object> { ["name"] = n, ["skipped"] = "exists" }); continue; }
                        Workset w = Autodesk.Revit.DB.Workset.Create(doc, n);
                        made.Add(new Dictionary<string, object> { ["name"] = w.Name, ["id"] = w.Id.IntegerValue });
                    }
                    return new Dictionary<string, object> { ["worksets"] = made };
                }
                case "rename":
                {
                    Workset w = Workset(doc, a.ReqStr("name"));
                    WorksetTable.RenameWorkset(doc, w.Id, a.ReqStr("new_name"));
                    return new Dictionary<string, object> { ["renamed"] = a.Str("new_name") };
                }
                case "set_active":
                {
                    Workset w = Workset(doc, a.ReqStr("name"));
                    doc.GetWorksetTable().SetActiveWorksetId(w.Id);
                    return new Dictionary<string, object> { ["active"] = w.Name };
                }
                case "visibility":
                {
                    Workset w = Workset(doc, a.ReqStr("name"));
                    View v = Rv.View(doc, a.Str("view"), c.UiDoc.ActiveView);
                    v.SetWorksetVisibility(w.Id, a.Bool("visible", true) ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
                    return new Dictionary<string, object> { ["view"] = v.Name, ["workset"] = w.Name, ["visible"] = a.Bool("visible", true) };
                }
                case "delete":
                {
                    Workset w = Workset(doc, a.ReqStr("name"));
                    Workset to = Workset(doc, a.ReqStr("move_to"));
                    var settings = new DeleteWorksetSettings(DeleteWorksetOption.MoveElementsToWorkset, to.Id);
                    if (!WorksetTable.CanDeleteWorkset(doc, w.Id, settings)) throw new ToolError($"Revit cannot delete '{w.Name}' now (active, borrowed, or the last workset).");
                    WorksetTable.DeleteWorkset(doc, w.Id, settings);
                    return new Dictionary<string, object> { ["deleted"] = w.Name, ["moved_to"] = to.Name };
                }
            }
            throw new ToolError("Unknown action " + action);
        }

        static object SetElementWorkset(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            Workset w = Workset(doc, a.ReqStr("workset"));
            int ok = 0;
            var failed = new List<object>();
            foreach (Element e in Rv.GetMany(doc, a.Ids("element_ids")))
            {
                Parameter p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (p == null || p.IsReadOnly) { failed.Add(new Dictionary<string, object> { ["id"] = Id.V(e.Id), ["why"] = "workset is fixed for this element" }); continue; }
                p.Set(w.Id.IntegerValue);
                ok++;
            }
            return new Dictionary<string, object> { ["moved"] = ok, ["workset"] = w.Name, ["failed"] = failed };
        }

        // ---------------------------------------------------------------- links

        static string LinkPath(Document doc, ElementId typeId)
        {
            try
            {
                ExternalFileReference r = (doc.GetElement(typeId) as ElementType)?.GetExternalFileReference();
                return r == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetAbsolutePath());
            }
            catch (Exception) { return null; }
        }

        static object ListLinks(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            var instances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            var rvt = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().Select(t => (object)new Dictionary<string, object>
            {
                ["type_id"] = Id.V(t.Id), ["name"] = t.Name, ["path"] = LinkPath(doc, t.Id),
                ["loaded"] = RevitLinkType.IsLoaded(doc, t.Id), ["nested"] = t.IsNestedLink,
                ["instances"] = instances.Where(i => Id.V(i.GetTypeId()) == Id.V(t.Id))
                    .Select(i => (object)new Dictionary<string, object> { ["id"] = Id.V(i.Id), ["name"] = i.Name, ["pinned"] = i.Pinned }).ToList(),
            }).ToList();
            var cad = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Select(i => (object)new Dictionary<string, object>
            {
                ["id"] = Id.V(i.Id), ["name"] = i.Category?.Name ?? Rv.SafeName(i), ["linked"] = i.IsLinked,
                ["view_specific"] = i.ViewSpecific, ["owner_view"] = i.ViewSpecific ? doc.GetElement(i.OwnerViewId)?.Name : null,
                ["pinned"] = i.Pinned, ["path"] = i.IsLinked ? LinkPath(doc, i.GetTypeId()) : null,
            }).ToList();
            return new Dictionary<string, object> { ["revit_links"] = rvt, ["cad"] = cad };
        }

        static RevitLinkType LinkType(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            RevitLinkType t = all.FirstOrDefault(x => string.Equals(x.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                              ?? all.FirstOrDefault(x => x.Name.IndexOf(name ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
            if (t == null) throw new ToolError($"No Revit link '{name}'. Links: " + string.Join(", ", all.Select(x => x.Name)));
            return t;
        }

        static object ManageLinks(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string action = a.ReqStr("action").ToLowerInvariant();
            var ctx = c;
            switch (action)
            {
                case "link":
                {
                    string path = a.ReqStr("path");
                    if (!File.Exists(path)) throw new ToolError("File not found: " + path);
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    return Rpc.InTransaction(ctx, "Link " + Path.GetFileName(path), () =>
                    {
                        if (ext == ".rvt")
                        {
                            LinkLoadResult r = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new RevitLinkOptions(false));
                            RevitLinkInstance inst = RevitLinkInstance.Create(doc, r.ElementId, ImportPlacement.Origin);
                            return (object)new Dictionary<string, object> { ["linked"] = Path.GetFileName(path), ["type_id"] = Id.V(r.ElementId), ["instance_id"] = Id.V(inst.Id), ["element_ids"] = new List<object> { Id.V(inst.Id) } };
                        }
                        if (ext != ".dwg" && ext != ".dxf" && ext != ".dgn" && ext != ".dwf" && ext != ".sat" && ext != ".skp")
                            throw new ToolError("Supported: .rvt, .dwg, .dxf, .dgn, .dwf, .sat, .skp");
                        View view = c.UiDoc.ActiveView;
                        bool thisView = a.Bool("this_view_only", false);
                        ElementId id;
                        if (ext == ".dgn")
                        {
                            doc.Link(path, new DGNImportOptions { Placement = ImportPlacement.Origin, ThisViewOnly = thisView }, view, out id);
                        }
                        else
                        {
                            doc.Link(path, new DWGImportOptions { Placement = ImportPlacement.Origin, ThisViewOnly = thisView }, view, out id);
                        }
                        return (object)new Dictionary<string, object> { ["linked"] = Path.GetFileName(path), ["instance_id"] = Id.V(id), ["element_ids"] = new List<object> { Id.V(id) } };
                    });
                }
                case "load":
                case "unload":
                case "reload":
                {
                    RevitLinkType t = LinkType(doc, a.ReqStr("name"));
                    // Revit refuses link load / unload inside a transaction
                    if (action == "unload") t.Unload(null);
                    else if (action == "load") t.Load();
                    else t.Reload();
                    return new Dictionary<string, object> { ["link"] = t.Name, ["action"] = action, ["loaded"] = RevitLinkType.IsLoaded(doc, t.Id) };
                }
                case "reload_from":
                {
                    RevitLinkType t = LinkType(doc, a.ReqStr("name"));
                    string path = a.ReqStr("path");
                    if (!File.Exists(path)) throw new ToolError("File not found: " + path);
                    t.LoadFrom(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new WorksetConfiguration());
                    return new Dictionary<string, object> { ["link"] = t.Name, ["path"] = path };
                }
                case "pin":
                case "unpin":
                    return Rpc.InTransaction(ctx, action == "pin" ? "Pin links" : "Unpin links", () =>
                    {
                        int n = 0;
                        foreach (Element e in Rv.GetMany(doc, a.Ids("element_ids"))) { e.Pinned = action == "pin"; n++; }
                        return (object)new Dictionary<string, object> { ["changed"] = n };
                    });
            }
            throw new ToolError("Unknown action " + action);
        }

        // ---------------------------------------------------------------- groups

        static GroupType GroupTypeOf(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
            GroupType g = all.FirstOrDefault(x => string.Equals(x.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (g == null) throw new ToolError($"No group type '{name}'. Group types: " + string.Join(", ", all.Select(x => x.Name).Take(40)));
            return g;
        }

        static object ManageGroups(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string action = a.ReqStr("action").ToLowerInvariant();
            switch (action)
            {
                case "list":
                {
                    var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().ToList();
                    var rows = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>().OrderBy(t => t.Name).Select(t => (object)new Dictionary<string, object>
                    {
                        ["type_id"] = Id.V(t.Id), ["name"] = t.Name, ["category"] = t.Category?.Name,
                        ["instances"] = groups.Where(g => Id.V(g.GetTypeId()) == Id.V(t.Id)).Select(g => (object)Id.V(g.Id)).ToList(),
                    }).ToList();
                    return new Dictionary<string, object> { ["group_types"] = rows };
                }
                case "create":
                {
                    List<ElementId> ids = a.Ids("element_ids");
                    Rv.GetMany(doc, ids);
                    Group g = doc.Create.NewGroup(ids);
                    if (a.Has("name")) g.GroupType.Name = a.Str("name");
                    return new Dictionary<string, object> { ["group_id"] = Id.V(g.Id), ["group_type"] = g.GroupType.Name, ["element_ids"] = new List<object> { Id.V(g.Id) } };
                }
                case "ungroup":
                {
                    var freed = new List<ElementId>();
                    foreach (Element e in Rv.GetMany(doc, a.Ids("element_ids")))
                    {
                        if (!(e is Group g)) throw new ToolError($"{Id.V(e.Id)} is not a group.");
                        freed.AddRange(g.UngroupMembers());
                    }
                    return new Dictionary<string, object> { ["members"] = freed.Count, ["element_ids"] = Rv.IdList(freed) };
                }
                case "place":
                {
                    GroupType t = GroupTypeOf(doc, a.ReqStr("group_type"));
                    Level lv = Rv.LevelOrDefault(doc, c.UiDoc.ActiveView, null);
                    var ids = new List<ElementId>();
                    foreach (XYZ p in a.Pts("points")) ids.Add(doc.Create.PlaceGroup(new XYZ(p.X, p.Y, lv.Elevation + p.Z), t).Id);
                    return new Dictionary<string, object> { ["placed"] = ids.Count, ["element_ids"] = Rv.IdList(ids) };
                }
                case "rename":
                {
                    GroupType t = GroupTypeOf(doc, a.ReqStr("group_type"));
                    t.Name = a.ReqStr("new_name");
                    return new Dictionary<string, object> { ["renamed"] = t.Name };
                }
            }
            throw new ToolError("Unknown action " + action);
        }

        // ---------------------------------------------------------------- materials

        static Material MaterialOf(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
            Material m = all.FirstOrDefault(x => string.Equals(x.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (m == null) throw new ToolError($"No material '{name}' (list_materials).");
            return m;
        }

        static object ListMaterials(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            IEnumerable<Material> mats = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>();
            if (a.Has("name_contains")) mats = mats.Where(m => m.Name.IndexOf(a.Str("name_contains"), StringComparison.OrdinalIgnoreCase) >= 0);
            HashSet<long> used = null;
            if (a.Bool("used", false))
            {
                used = new HashSet<long>();
                foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    if (e.Category == null || e.Category.CategoryType != CategoryType.Model) continue;
                    try { foreach (ElementId id in e.GetMaterialIds(false)) used.Add(Id.V(id)); } catch (Exception) { }
                }
                mats = mats.Where(m => used.Contains(Id.V(m.Id)));
            }
            var rows = mats.OrderBy(m => m.Name).Take(a.Int("limit", 300)).Select(m => (object)new Dictionary<string, object>
            {
                ["id"] = Id.V(m.Id), ["name"] = m.Name, ["class"] = m.MaterialClass,
                ["rgb"] = m.Color != null && m.Color.IsValid ? new[] { (int)m.Color.Red, m.Color.Green, m.Color.Blue } : null,
                ["transparency"] = m.Transparency,
            }).ToList();
            return new Dictionary<string, object> { ["materials"] = rows };
        }

        static object ManageMaterial(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string action = a.ReqStr("action").ToLowerInvariant();
            string name = a.ReqStr("name");
            Material m;
            switch (action)
            {
                case "create":
                    m = a.Has("from") ? MaterialOf(doc, a.Str("from")).Duplicate(name) : doc.GetElement(Material.Create(doc, name)) as Material;
                    ApplyMaterial(m, a);
                    return new Dictionary<string, object> { ["id"] = Id.V(m.Id), ["name"] = m.Name, ["element_ids"] = new List<object> { Id.V(m.Id) } };
                case "rename":
                    m = MaterialOf(doc, name);
                    m.Name = a.ReqStr("new_name");
                    return new Dictionary<string, object> { ["renamed"] = m.Name };
                case "set":
                    m = MaterialOf(doc, name);
                    ApplyMaterial(m, a);
                    return new Dictionary<string, object> { ["material"] = m.Name };
                case "delete":
                    m = MaterialOf(doc, name);
                    doc.Delete(m.Id);
                    return new Dictionary<string, object> { ["deleted"] = name };
            }
            throw new ToolError("Unknown action " + action);
        }

        static void ApplyMaterial(Material m, Args a)
        {
            List<string> rgb = a.Strs("rgb");
            if (rgb.Count == 3) m.Color = new Color((byte)Args.ToDouble(rgb[0], "rgb"), (byte)Args.ToDouble(rgb[1], "rgb"), (byte)Args.ToDouble(rgb[2], "rgb"));
            if (a.Has("transparency")) m.Transparency = Math.Max(0, Math.Min(100, a.Int("transparency", 0)));
            if (a.Has("shininess")) m.Shininess = Math.Max(0, Math.Min(128, a.Int("shininess", 0)));
            if (a.Has("smoothness")) m.Smoothness = Math.Max(0, Math.Min(100, a.Int("smoothness", 0)));
            if (a.Has("material_class")) m.MaterialClass = a.Str("material_class");
        }

        static readonly BuiltInCategory[] TakeoffDefault =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation,
        };

        static object MaterialQuantities(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            List<string> cats = a.Strs("categories");
            var bics = cats.Count > 0 ? cats.Select(n => Rv.Bic(Rv.Category(doc, n))).ToList() : TakeoffDefault.ToList();
            IEnumerable<Element> els = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(bics)).WhereElementIsNotElementType();
            if (a.Has("level")) els = els.Where(e => string.Equals(Rv.LevelName(e), a.Str("level"), StringComparison.OrdinalIgnoreCase));
            bool byCat = a.Bool("by_category", false);
            var totals = new Dictionary<string, double[]>();
            int counted = 0;
            foreach (Element e in els)
            {
                ICollection<ElementId> mids;
                try { mids = e.GetMaterialIds(false); } catch (Exception) { continue; }
                counted++;
                foreach (ElementId mid in mids)
                {
                    string key = (doc.GetElement(mid)?.Name ?? "(none)") + (byCat ? " | " + e.Category?.Name : "");
                    if (!totals.TryGetValue(key, out double[] t)) totals[key] = t = new double[3];
                    try { t[0] += e.GetMaterialArea(mid, false) * 0.09290304; } catch (Exception) { }
                    try { t[1] += e.GetMaterialVolume(mid) * 0.028316846592; } catch (Exception) { }
                    t[2] += 1;
                }
            }
            var rows = totals.OrderByDescending(kv => kv.Value[1]).Select(kv => (object)new Dictionary<string, object>
            {
                ["material"] = kv.Key, ["area_m2"] = Math.Round(kv.Value[0], 3), ["volume_m3"] = Math.Round(kv.Value[1], 4), ["elements"] = (int)kv.Value[2],
            }).ToList();
            return new Dictionary<string, object> { ["elements_counted"] = counted, ["materials"] = rows };
        }

        // ---------------------------------------------------------------- revisions

        static Revision RevisionOf(Document doc, string key)
        {
            var all = Revision.GetAllRevisionIds(doc).Select(doc.GetElement).OfType<Revision>().ToList();
            Revision r = (long.TryParse(key, out long v) ? all.FirstOrDefault(x => Id.V(x.Id) == v) : null)
                         ?? all.FirstOrDefault(x => string.Equals(x.Description, key, StringComparison.OrdinalIgnoreCase))
                         ?? all.FirstOrDefault(x => (x.Description ?? "").IndexOf(key ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
            if (r == null) throw new ToolError($"No revision '{key}' (manage_revisions action=list).");
            return r;
        }

        static List<ViewSheet> SheetsOf(Document doc, Args a)
        {
            List<string> keys = a.Strs("sheets");
            if (keys.Contains("*")) return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
            return keys.Select(k => SheetTools.Sheet(doc, k)).ToList();
        }

        static object ManageRevisions(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string action = a.ReqStr("action").ToLowerInvariant();
            switch (action)
            {
                case "list":
                {
                    var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
                    var rows = Revision.GetAllRevisionIds(doc).Select(doc.GetElement).OfType<Revision>().Select(r => (object)new Dictionary<string, object>
                    {
                        ["id"] = Id.V(r.Id), ["sequence"] = r.SequenceNumber, ["description"] = r.Description, ["date"] = r.RevisionDate,
                        ["issued"] = r.Issued, ["issued_by"] = r.IssuedBy, ["issued_to"] = r.IssuedTo,
                        ["sheets"] = sheets.Where(s => s.GetAllRevisionIds().Any(i => Id.V(i) == Id.V(r.Id))).Select(s => (object)s.SheetNumber).ToList(),
                    }).ToList();
                    return new Dictionary<string, object> { ["revisions"] = rows };
                }
                case "create":
                {
                    Revision r = Revision.Create(doc);
                    if (a.Has("description")) r.Description = a.Str("description");
                    if (a.Has("date")) r.RevisionDate = a.Str("date");
                    if (a.Has("issued_by")) r.IssuedBy = a.Str("issued_by");
                    if (a.Has("issued_to")) r.IssuedTo = a.Str("issued_to");
                    return new Dictionary<string, object> { ["id"] = Id.V(r.Id), ["sequence"] = r.SequenceNumber, ["element_ids"] = new List<object> { Id.V(r.Id) } };
                }
                case "add_to_sheets":
                case "remove_from_sheets":
                {
                    Revision r = RevisionOf(doc, a.ReqStr("revision"));
                    int n = 0;
                    foreach (ViewSheet s in SheetsOf(doc, a))
                    {
                        var ids = s.GetAdditionalRevisionIds().ToList();
                        bool has = ids.Any(i => Id.V(i) == Id.V(r.Id));
                        if (action == "add_to_sheets" && !has) ids.Add(r.Id);
                        else if (action == "remove_from_sheets" && has) ids = ids.Where(i => Id.V(i) != Id.V(r.Id)).ToList();
                        else continue;
                        s.SetAdditionalRevisionIds(ids);
                        n++;
                    }
                    return new Dictionary<string, object> { ["revision"] = r.Description, ["sheets_changed"] = n };
                }
                case "issue":
                {
                    Revision r = RevisionOf(doc, a.ReqStr("revision"));
                    if (a.Has("issued_by")) r.IssuedBy = a.Str("issued_by");
                    if (a.Has("issued_to")) r.IssuedTo = a.Str("issued_to");
                    r.Issued = true;
                    return new Dictionary<string, object> { ["issued"] = r.Description };
                }
            }
            throw new ToolError("Unknown action " + action);
        }

        // ---------------------------------------------------------------- extensible storage

        static readonly Guid StorageGuid = new Guid("6E3A51C2-7D94-4B2F-9C1E-3A8F5D20B7C4");

        static Schema StorageSchema(bool create)
        {
            Schema s = Schema.Lookup(StorageGuid);
            if (s != null || !create) return s;
            var b = new SchemaBuilder(StorageGuid);
            b.SetSchemaName("T3LabMcpStorage");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField("data", typeof(string));
            return b.Finish();
        }

        static object ProjectStorage(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            if (doc.IsFamilyDocument) throw new ToolError("Project storage is for projects, not families.");
            string action = a.ReqStr("action").ToLowerInvariant();
            ProjectInfo pi = doc.ProjectInformation;
            Schema schema = StorageSchema(action != "get");
            Dictionary<string, object> data = new Dictionary<string, object>();
            if (schema != null)
            {
                Entity ent = pi.GetEntity(schema);
                if (ent.IsValid())
                {
                    string json = ent.Get<string>("data");
                    if (!string.IsNullOrEmpty(json) && Json.Parse(json) is Dictionary<string, object> d) data = d;
                }
            }
            switch (action)
            {
                case "get":
                    if (a.Has("key")) return new Dictionary<string, object> { ["key"] = a.Str("key"), ["value"] = data.TryGetValue(a.Str("key"), out object v) ? v : null, ["found"] = data.ContainsKey(a.Str("key")) };
                    return new Dictionary<string, object> { ["keys"] = data.Keys.ToList(), ["data"] = data };
                case "set":
                case "delete":
                {
                    string key = a.ReqStr("key");
                    if (action == "set") data[key] = a.Raw("value");
                    else data.Remove(key);
                    string json = Json.Write(data);
                    if (json.Length > 1_000_000) throw new ToolError("Project storage is limited to 1 MB.");
                    var ent = new Entity(schema);
                    ent.Set("data", json);
                    pi.SetEntity(ent);
                    return new Dictionary<string, object> { ["key"] = key, ["action"] = action, ["keys"] = data.Keys.ToList() };
                }
            }
            throw new ToolError("Unknown action " + action);
        }
    }
}
