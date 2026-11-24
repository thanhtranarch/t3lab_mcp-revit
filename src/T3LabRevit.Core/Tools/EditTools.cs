using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    /// <summary>Bulk edits: purge, join / split, rehost, rename, tag all, text notes, line-based families, copy from another model.</summary>
    internal static class EditTools
    {
        public static void Register()
        {
            Registry.Add("purge_unused", "Purge unused", ToolKind.Write,
                "Delete what nothing uses. dry_run=true (default) only lists it. Revit 2024+: everything Purge Unused would remove (one " +
                "pass); 2022-2023: unused family types, view templates, view filters and materials. categories narrows it (Revit 2024+).",
                Obj(P("dry_run", Bool("List only (default true)")), P("categories", Strs("Only these categories (Revit 2024+)")), Limit),
                Purge, destructive: true);

            Registry.Add("join_geometry", "Join, unjoin or switch join", ToolKind.Write,
                "action=join: every pair of the given elements whose boxes touch is joined (or pairs [[a, b], ...]); unjoin; switch " +
                "(switch join order of pairs); check (list which pairs are joined).",
                ObjR("action", P("action", Enum("What to do", "join", "unjoin", "switch", "check")), Ids,
                    P("pairs", "{'type':'array','items':{'type':'array','items':{'type':'integer'}},'description':'Element id pairs'}")),
                JoinGeometry);

            Registry.Add("split_element", "Split walls, beams, pipes or ducts", ToolKind.Write,
                "Split a wall, beam (or other line-based element), pipe or duct at points (mm, projected onto it). Returns the new pieces.",
                ObjR("element_id,points", P("element_id", Int("Element to split")), P("points", Pts("Split points [x, y] or [x, y, z] mm"))),
                Split);

            Registry.Add("rehost_to_level", "Move elements to another level", ToolKind.Write,
                "Change the level of walls, floors, ceilings, columns, family instances, rooms... keeping them where they are (the " +
                "offset is adjusted). keep_position=false keeps the offset instead (the element moves with the level).",
                ObjR("element_ids,level", Ids, P("level", Str("Target level name or id")), P("keep_position", Bool("Keep the elevation (default true)"))),
                Rehost);

            Registry.Add("rename_elements", "Rename views, sheets, types, levels, rooms...", ToolKind.Write,
                "Rename many elements at once: names [{element_id, name}], or element_ids + find / replace (regex=true for a regular " +
                "expression) and / or prefix / suffix. Rooms and spaces rename their Name; sheets take number=true to change the number. " +
                "dry_run=true shows the new names only.",
                Obj(P("names", "{'type':'array','items':{'type':'object','properties':{'element_id':{'type':'integer'},'name':{'type':'string'}}},'description':'Exact new names'}"),
                    Ids, P("find", Str("Text to find")), P("replace", Str("Replacement")), P("regex", Bool("find is a regular expression")),
                    P("prefix", Str("Add before")), P("suffix", Str("Add after")), P("number", Bool("Sheets: change the sheet number")),
                    P("dry_run", Bool("Preview only"))),
                Rename);

            Registry.Add("tag_all_in_view", "Tag everything not tagged", ToolKind.Write,
                "Tag every element of the categories visible in a view (default active) that has no tag there yet: rooms get room tags, " +
                "the rest category tags. leader=true adds leaders.",
                ObjR("categories", P("categories", Strs("Categories, e.g. Rooms, Doors, Walls")), P("view", Str("View name or id")),
                    P("leader", Bool("Add leaders"))),
                TagAll);

            Registry.Add("edit_text_notes", "Find and replace in text notes", ToolKind.Write,
                "Find / replace text in text notes of a view (default all views): find, replace, regex, case_sensitive. dry_run=true lists the matches.",
                ObjR("find", P("find", Str("Text to find")), P("replace", Str("Replacement (default empty)")), P("regex", Bool("Regular expression")),
                    P("case_sensitive", Bool("Match case")), P("view", Str("Only this view")), P("dry_run", Bool("Preview only"))),
                EditTextNotes, destructive: true);

            Registry.Add("place_line_based", "Place beams and line-based families", ToolKind.Write,
                "Place structural framing (beams, braces) or other line-based families along lines [{start, end}] mm on a level, " +
                "type = Family: Type label.",
                ObjR("type,lines", P("type", Str("Family type label or name")), P("lines", "{'type':'array','items':{'type':'object'},'description':'Lines: start, end [x, y] or [x, y, z] mm, z above the level'}"),
                    P("level", Str("Reference level")), P("brace", Bool("Place as braces instead of beams"))),
                PlaceLineBased);

            Registry.Add("transfer_elements", "Copy from another open model", ToolKind.Write,
                "Copy elements or types from another open model (from_document title) into the active one: element_ids from that model, " +
                "or categories / classes to copy all of them (e.g. class WallType for wall types, FamilySymbol + category Doors). " +
                "Types with the same name keep the destination version.",
                ObjR("from_document", P("from_document", Str("Title of the source model")), Ids, P("categories", Strs("Copy every element / type of these")),
                    P("class", Str("Revit class, e.g. WallType, FloorType, FamilySymbol, ViewSheet")), P("types", Bool("Copy types (default true when class ends with Type)"))),
                Transfer);

            Registry.Add("export_room_data", "Room data", ToolKind.Read,
                "Rooms (or spaces, kind=spaces) with number, name, level, area m2, perimeter mm, volume m3, department, finishes and any " +
                "extra parameters; csv_path writes a CSV for Excel.",
                Obj(P("kind", Enum("rooms or spaces", "rooms", "spaces")), P("level", Str("Only this level")), P("fields", Strs("Extra parameter names")),
                    P("csv_path", Str("Optional .csv path")), P("delimiter", Str("CSV delimiter")), Limit),
                RoomData, writesFiles: true);

            Registry.Add("schedule_roundtrip", "Schedule to CSV and back", ToolKind.Write,
                "action=export: every element of a schedule with its field values and element_id to a CSV (edit it in Excel); " +
                "action=import: read that CSV back and set the changed values (dry_run=true shows the changes). Read-only fields and " +
                "unchanged values are skipped.",
                ObjR("action,schedule", P("action", Enum("export or import", "export", "import")), P("schedule", Str("Schedule name or id")),
                    P("csv_path", Str("CSV path")), P("delimiter", Str("CSV delimiter, default comma")), P("dry_run", Bool("import: preview only"))),
                ScheduleRoundtrip);
        }

        // ---------------------------------------------------------------- purge

        static object Purge(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            bool dry = a.Bool("dry_run", true);
            var ids = new List<ElementId>();
#if REVIT2024_OR_GREATER
            var cats = new HashSet<ElementId>(a.Strs("categories").Select(n => Rv.Category(doc, n).Id));
            ids.AddRange(doc.GetUnusedElements(cats));
#else
            var usedTypes = new HashSet<long>(new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => Id.V(e.GetTypeId())));
            ids.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => !usedTypes.Contains(Id.V(s.Id)) && s.Family != null && !s.Family.IsInPlace && Rv.Bic(s.Category) != BuiltInCategory.OST_TitleBlocks)
                .Select(s => s.Id));
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var usedTemplates = new HashSet<long>(views.Select(v => Id.V(v.ViewTemplateId)));
            ids.AddRange(views.Where(v => v.IsTemplate && !usedTemplates.Contains(Id.V(v.Id))).Select(v => v.Id));
            var usedFilters = new HashSet<long>(views.Where(v => v.AreGraphicsOverridesAllowed()).SelectMany(v => { try { return v.GetFilters(); } catch (Exception) { return new List<ElementId>(); } }).Select(Id.V));
            ids.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Where(f => !usedFilters.Contains(Id.V(f.Id))).Select(f => f.Id));
#endif
            var byCat = ids.Select(doc.GetElement).Where(e => e != null).GroupBy(e => e.Category?.Name ?? e.GetType().Name)
                .ToDictionary(g => g.Key, g => (object)g.Count());
            var sample = ids.Take(a.Int("limit", 100)).Select(doc.GetElement).Where(e => e != null)
                .Select(e => (object)new Dictionary<string, object> { ["id"] = Id.V(e.Id), ["category"] = e.Category?.Name, ["name"] = Rv.SafeName(e) }).ToList();
            if (dry) return new Dictionary<string, object> { ["dry_run"] = true, ["unused"] = ids.Count, ["by_category"] = byCat, ["sample"] = sample };
            int deleted = 0;
            var failed = 0;
            foreach (ElementId id in ids)
            {
                try { if (doc.GetElement(id) != null) { doc.Delete(id); deleted++; } }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { failed++; }
            }
            return new Dictionary<string, object> { ["deleted"] = deleted, ["failed"] = failed, ["by_category"] = byCat };
        }

        // ---------------------------------------------------------------- join / split

        static List<Tuple<Element, Element>> Pairs(Document doc, Args a, bool touchingOnly)
        {
            var pairs = new List<Tuple<Element, Element>>();
            foreach (object raw in a.List("pairs") ?? new object[0])
            {
                var p = new Args(new Dictionary<string, object> { ["v"] = raw }).Ids("v");
                if (p.Count != 2) throw new ToolError("Each pair needs 2 element ids.");
                pairs.Add(Tuple.Create(Rv.Get(doc, p[0]), Rv.Get(doc, p[1])));
            }
            List<Element> els = Rv.GetMany(doc, a.Ids("element_ids"));
            if (els.Count > 300) throw new ToolError("At most 300 elements per call.");
            for (int i = 0; i < els.Count; i++)
            {
                BoundingBoxXYZ bi = els[i].get_BoundingBox(null);
                for (int j = i + 1; j < els.Count; j++)
                {
                    if (touchingOnly)
                    {
                        BoundingBoxXYZ bj = els[j].get_BoundingBox(null);
                        if (bi == null || bj == null) continue;
                        double tol = U.Ft(1);
                        if (bi.Max.X + tol < bj.Min.X || bj.Max.X + tol < bi.Min.X || bi.Max.Y + tol < bj.Min.Y || bj.Max.Y + tol < bi.Min.Y ||
                            bi.Max.Z + tol < bj.Min.Z || bj.Max.Z + tol < bi.Min.Z) continue;
                    }
                    pairs.Add(Tuple.Create(els[i], els[j]));
                }
            }
            return pairs;
        }

        static object JoinGeometry(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string action = a.ReqStr("action").ToLowerInvariant();
            int done = 0;
            var skipped = new List<object>();
            var joined = new List<object>();
            foreach (var p in Pairs(doc, a, action == "join"))
            {
                bool isJoined = JoinGeometryUtils.AreElementsJoined(doc, p.Item1, p.Item2);
                try
                {
                    switch (action)
                    {
                        case "check": if (isJoined) joined.Add(new[] { Id.V(p.Item1.Id), Id.V(p.Item2.Id) }); continue;
                        case "join": if (isJoined) continue; JoinGeometryUtils.JoinGeometry(doc, p.Item1, p.Item2); break;
                        case "unjoin": if (!isJoined) continue; JoinGeometryUtils.UnjoinGeometry(doc, p.Item1, p.Item2); break;
                        case "switch": if (!isJoined) continue; JoinGeometryUtils.SwitchJoinOrder(doc, p.Item1, p.Item2); break;
                        default: throw new ToolError("Unknown action " + action);
                    }
                    done++;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    if (skipped.Count < 30) skipped.Add(new Dictionary<string, object> { ["pair"] = new[] { Id.V(p.Item1.Id), Id.V(p.Item2.Id) }, ["why"] = ex.Message });
                }
            }
            if (action == "check") return new Dictionary<string, object> { ["joined_pairs"] = joined };
            return new Dictionary<string, object> { ["action"] = action, ["pairs_changed"] = done, ["skipped"] = skipped };
        }

        static object Split(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            Element e = Rv.Get(doc, a.IdOrNull("element_id"));
            var pieces = new List<ElementId> { e.Id };
            foreach (XYZ raw in a.Pts("points"))
            {
                // split the piece that holds the point
                Element target = null;
                Curve curve = null;
                double tBest = 0;
                foreach (ElementId pid in pieces)
                {
                    if (!(doc.GetElement(pid)?.Location is LocationCurve lc)) continue;
                    XYZ q = new XYZ(raw.X, raw.Y, lc.Curve.GetEndPoint(0).Z);
                    IntersectionResult r = lc.Curve.Project(q);
                    if (r == null) continue;
                    double t = lc.Curve.ComputeNormalizedParameter(r.Parameter);
                    if (t > 0.001 && t < 0.999 && r.Distance < U.Ft(500)) { target = doc.GetElement(pid); curve = lc.Curve; tBest = r.Parameter; break; }
                }
                if (target == null) throw new ToolError($"Point {string.Join(",", U.Pt(raw))} is not on the element (within 500 mm, away from its ends).");
                XYZ at = curve.Evaluate(tBest, false);
                ElementId made;
                if (target is MEPCurve && Rv.Bic(target.Category) == BuiltInCategory.OST_PipeCurves)
                    made = Autodesk.Revit.DB.Plumbing.PlumbingUtils.BreakCurve(doc, target.Id, at);
                else if (target is MEPCurve && Rv.Bic(target.Category) == BuiltInCategory.OST_DuctCurves)
                    made = Autodesk.Revit.DB.Mechanical.MechanicalUtils.BreakCurve(doc, target.Id, at);
                else
                {
                    // copy in place, then each keeps one side of the point
                    if (!(curve is Line)) throw new ToolError("Only straight walls and beams can be split (curved ones: use send_code_to_revit).");
                    XYZ s = curve.GetEndPoint(0), en = curve.GetEndPoint(1);
                    made = ElementTransformUtils.CopyElement(doc, target.Id, XYZ.Zero).First();
                    ((LocationCurve)target.Location).Curve = Line.CreateBound(s, at);
                    ((LocationCurve)doc.GetElement(made).Location).Curve = Line.CreateBound(at, en);
                }
                pieces.Add(made);
            }
            return new Dictionary<string, object> { ["pieces"] = pieces.Count, ["element_ids"] = Rv.IdList(pieces) };
        }

        // ---------------------------------------------------------------- rehost

        static readonly BuiltInParameter[][] LevelOffset =
        {
            new[] { BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.WALL_BASE_OFFSET },
            new[] { BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM },
            new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM },
            new[] { BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM },
            new[] { BuiltInParameter.LEVEL_PARAM, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM },
            new[] { BuiltInParameter.LEVEL_PARAM, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM },
            new[] { BuiltInParameter.ROOF_BASE_LEVEL_PARAM, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM },
            new[] { BuiltInParameter.ROOM_LEVEL_ID, BuiltInParameter.ROOM_LOWER_OFFSET },
            new[] { BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM },
        };

        static object Rehost(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            Level target = Rv.Level(doc, a.ReqStr("level"));
            bool keep = a.Bool("keep_position", true);
            int ok = 0;
            var failed = new List<object>();
            foreach (Element e in Rv.GetMany(doc, a.Ids("element_ids")))
            {
                bool done = false;
                foreach (BuiltInParameter[] pair in LevelOffset)
                {
                    Parameter lp = e.get_Parameter(pair[0]);
                    if (lp == null || lp.IsReadOnly || lp.StorageType != StorageType.ElementId) continue;
                    Level from = doc.GetElement(lp.AsElementId()) as Level;
                    Parameter op = e.get_Parameter(pair[1]);
                    if (from == null || (op != null && op.IsReadOnly)) continue;
                    double delta = keep ? from.Elevation - target.Elevation : 0;
                    double offset = op != null ? op.AsDouble() : 0;
                    lp.Set(target.Id);
                    if (op != null && Math.Abs(delta) > 1e-9) op.Set(offset + delta);
                    done = true;
                    break;
                }
                if (done) ok++;
                else failed.Add(new Dictionary<string, object> { ["id"] = Id.V(e.Id), ["category"] = e.Category?.Name, ["why"] = "no editable level parameter" });
            }
            return new Dictionary<string, object> { ["moved"] = ok, ["level"] = target.Name, ["failed"] = failed };
        }

        // ---------------------------------------------------------------- rename

        static string CurrentName(Element e, bool number)
        {
            if (e is ViewSheet s && number) return s.SheetNumber;
            if (e is SpatialElement) return e.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? Rv.SafeName(e);
            return Rv.SafeName(e);
        }

        static void SetName(Element e, string name, bool number)
        {
            if (e is ViewSheet s && number) { s.SheetNumber = name; return; }
            if (e is SpatialElement) { e.get_Parameter(BuiltInParameter.ROOM_NAME).Set(name); return; }
            e.Name = name;
        }

        static object Rename(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            bool number = a.Bool("number", false), dry = a.Bool("dry_run", false);
            var jobs = new List<Tuple<Element, string>>();
            foreach (Args n in a.Objs("names")) jobs.Add(Tuple.Create(Rv.Get(doc, Id.Make(Args.ToLong(n.Raw("element_id"), "element_id"))), n.ReqStr("name")));
            if (a.Has("element_ids"))
            {
                string find = a.Str("find"), repl = a.Str("replace") ?? "", pre = a.Str("prefix") ?? "", suf = a.Str("suffix") ?? "";
                Regex rx = a.Bool("regex", false) && !string.IsNullOrEmpty(find) ? new Regex(find) : null;
                foreach (Element e in Rv.GetMany(doc, a.Ids("element_ids")))
                {
                    string cur = CurrentName(e, number) ?? "";
                    string nw = rx != null ? rx.Replace(cur, repl) : string.IsNullOrEmpty(find) ? cur : cur.Replace(find, repl);
                    jobs.Add(Tuple.Create(e, pre + nw + suf));
                }
            }
            if (jobs.Count == 0) throw new ToolError("Give names [{element_id, name}] or element_ids with find / replace / prefix / suffix.");
            var rows = new List<object>();
            int ok = 0;
            foreach (var j in jobs)
            {
                string old = CurrentName(j.Item1, number);
                var row = new Dictionary<string, object> { ["id"] = Id.V(j.Item1.Id), ["old"] = old, ["new"] = j.Item2 };
                if (old == j.Item2) { row["skipped"] = "same"; rows.Add(row); continue; }
                if (!dry)
                {
                    try { SetName(j.Item1, j.Item2, number); ok++; }
                    catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException) { row["error"] = ex.Message; }
                }
                if (rows.Count < 200) rows.Add(row);
            }
            return new Dictionary<string, object> { ["dry_run"] = dry, ["renamed"] = ok, ["rows"] = rows };
        }

        // ---------------------------------------------------------------- tag all

        static object TagAll(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            View view = Rv.View(doc, a.Str("view"), c.UiDoc.ActiveView);
            bool leader = a.Bool("leader", false);
            var tagged = new HashSet<long>();
            foreach (IndependentTag t in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                foreach (ElementId id in t.GetTaggedLocalElementIds()) tagged.Add(Id.V(id));
            foreach (RoomTag rt in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags).OfType<RoomTag>())
                if (rt.Room != null) tagged.Add(Id.V(rt.Room.Id));
            var made = new List<ElementId>();
            var failed = new Dictionary<string, object>();
            foreach (string catName in a.Strs("categories"))
            {
                Category cat = Rv.Category(doc, catName);
                foreach (Element e in new FilteredElementCollector(doc, view.Id).OfCategoryId(cat.Id).WhereElementIsNotElementType())
                {
                    if (tagged.Contains(Id.V(e.Id))) continue;
                    try
                    {
                        if (e is Room room)
                        {
                            if (!(room.Location is LocationPoint lp)) continue;
                            RoomTag rt = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(lp.Point.X, lp.Point.Y), view.Id);
                            if (rt != null) made.Add(rt.Id);
                            continue;
                        }
                        BoundingBoxXYZ b = e.get_BoundingBox(view);
                        if (b == null) continue;
                        IndependentTag tag = IndependentTag.Create(doc, view.Id, new Reference(e), leader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, (b.Min + b.Max) / 2);
                        made.Add(tag.Id);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        failed[cat.Name] = ex.Message + " (is a tag family loaded for " + cat.Name + "?)";
                    }
                }
            }
            return new Dictionary<string, object> { ["view"] = view.Name, ["tags_created"] = made.Count, ["element_ids"] = Rv.IdList(made), ["failed"] = failed };
        }

        // ---------------------------------------------------------------- text notes

        static object EditTextNotes(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            FilteredElementCollector col = a.Has("view") ? new FilteredElementCollector(doc, Rv.View(doc, a.Str("view"), null).Id) : new FilteredElementCollector(doc);
            string find = a.ReqStr("find"), repl = a.Str("replace") ?? "";
            var opts = a.Bool("case_sensitive", false) ? RegexOptions.None : RegexOptions.IgnoreCase;
            var rx = new Regex(a.Bool("regex", false) ? find : Regex.Escape(find), opts);
            bool dry = a.Bool("dry_run", false);
            var rows = new List<object>();
            int changed = 0;
            foreach (TextNote t in col.OfClass(typeof(TextNote)).Cast<TextNote>())
            {
                string text = t.Text ?? "";
                if (!rx.IsMatch(text)) continue;
                string nw = rx.Replace(text, repl);
                if (rows.Count < 100) rows.Add(new Dictionary<string, object> { ["id"] = Id.V(t.Id), ["view"] = doc.GetElement(t.OwnerViewId)?.Name, ["old"] = text.Trim(), ["new"] = nw.Trim() });
                if (!dry) { t.Text = nw; changed++; }
            }
            return new Dictionary<string, object> { ["dry_run"] = dry, ["matches"] = rows.Count, ["changed"] = changed, ["rows"] = rows };
        }

        // ---------------------------------------------------------------- line-based

        static object PlaceLineBased(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            FamilySymbol sym = Rv.TypeOf<FamilySymbol>(doc, a.ReqStr("type"));
            if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
            Level lv = Rv.LevelOrDefault(doc, c.UiDoc.ActiveView, a.Str("level"));
            bool framing = Rv.Bic(sym.Category) == BuiltInCategory.OST_StructuralFraming;
            StructuralType st = framing ? (a.Bool("brace", false) ? StructuralType.Brace : StructuralType.Beam) : StructuralType.NonStructural;
            var ids = new List<ElementId>();
            foreach (Args l in a.Objs("lines"))
            {
                // z is the height above the level
                XYZ p0 = l.ReqPt("start"), p1 = l.ReqPt("end");
                p0 = new XYZ(p0.X, p0.Y, p0.Z + lv.Elevation);
                p1 = new XYZ(p1.X, p1.Y, p1.Z + lv.Elevation);
                ids.Add(doc.Create.NewFamilyInstance(Line.CreateBound(p0, p1), sym, lv, st).Id);
            }
            return new Dictionary<string, object> { ["created"] = ids.Count, ["type"] = Rv.TypeLabel(sym), ["level"] = lv.Name, ["element_ids"] = Rv.IdList(ids) };
        }

        // ---------------------------------------------------------------- transfer

        sealed class UseDestination : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
        }

        static object Transfer(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            Document src = TemplateTools.OpenDocument(c.App, a.ReqStr("from_document"));
            if (src.Equals(doc)) throw new ToolError("from_document is the active model.");
            var ids = new List<ElementId>(a.Ids("element_ids"));
            foreach (ElementId id in ids) if (src.GetElement(id) == null) throw new ToolError($"No element {Id.V(id)} in {src.Title}.");
            if (a.Has("categories") || a.Has("class"))
            {
                string cls = a.Str("class");
                bool types = a.Bool("types", cls != null && cls.EndsWith("Type", StringComparison.Ordinal) || cls == "FamilySymbol");
                FilteredElementCollector col = new FilteredElementCollector(src);
                col = types ? col.WhereElementIsElementType() : col.WhereElementIsNotElementType();
                List<string> cats = a.Strs("categories");
                if (cats.Count > 0) col = col.WherePasses(new ElementMulticategoryFilter(cats.Select(n => Rv.Bic(Rv.Category(src, n))).ToList()));
                if (cls != null) col = col.OfClass(QueryTools.ApiClass(cls));
                ids.AddRange(col.ToElementIds());
            }
            if (ids.Count == 0) throw new ToolError("Nothing to copy: give element_ids, categories or class.");
            var opts = new CopyPasteOptions();
            opts.SetDuplicateTypeNamesHandler(new UseDestination());
            ICollection<ElementId> made = ElementTransformUtils.CopyElements(src, ids, doc, Transform.Identity, opts);
            return new Dictionary<string, object> { ["from"] = src.Title, ["requested"] = ids.Count, ["copied"] = made.Count, ["element_ids"] = Rv.IdList(made.Take(500)) };
        }

        // ---------------------------------------------------------------- rooms

        internal static string CsvLine(IEnumerable<object> cells, string sep) =>
            string.Join(sep, cells.Select(v =>
            {
                string s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                return s.Contains(sep) || s.Contains("\"") || s.Contains("\n") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            }));

        static object RoomData(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            bool spaces = string.Equals(a.Str("kind"), "spaces", StringComparison.OrdinalIgnoreCase);
            IEnumerable<SpatialElement> els = new FilteredElementCollector(doc).OfCategory(spaces ? BuiltInCategory.OST_MEPSpaces : BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().OfType<SpatialElement>();
            if (a.Has("level")) els = els.Where(r => string.Equals(r.Level?.Name, a.Str("level"), StringComparison.OrdinalIgnoreCase));
            List<string> extra = a.Strs("fields");
            string[] fixedFields = { "id", "number", "name", "level", "area_m2", "perimeter_mm", "volume_m3", "department", "floor_finish", "wall_finish", "ceiling_finish", "placed" };
            var rows = new List<object[]>();
            foreach (SpatialElement r in els.OrderBy(x => x.Level?.Elevation ?? 0).ThenBy(x => x.Number))
            {
                var row = new List<object>
                {
                    Id.V(r.Id), r.Number, r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(), r.Level?.Name,
                    Math.Round(r.Area * 0.09290304, 3), U.Mm(r.Perimeter),
                    Math.Round((r.get_Parameter(BuiltInParameter.ROOM_VOLUME)?.AsDouble() ?? 0) * 0.028316846592, 3),
                    r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString(),
                    r.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.AsString(),
                    r.get_Parameter(BuiltInParameter.ROOM_FINISH_WALL)?.AsString(),
                    r.get_Parameter(BuiltInParameter.ROOM_FINISH_CEILING)?.AsString(),
                    r.Location != null,
                };
                foreach (string f in extra) { Parameter p = Rv.Param(r, f); row.Add(p == null ? null : p.StorageType == StorageType.ElementId ? Rv.SafeValueString(p) : Rv.Value(p)); }
                rows.Add(row.ToArray());
            }
            string[] header = fixedFields.Concat(extra).ToArray();
            string csv = a.Str("csv_path");
            if (!string.IsNullOrEmpty(csv))
            {
                string sep = a.Str("delimiter") ?? ",";
                var sb = new StringBuilder();
                sb.AppendLine(CsvLine(header, sep));
                foreach (object[] row in rows) sb.AppendLine(CsvLine(row, sep));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csv)));
                File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
            }
            int limit = a.Int("limit", 500);
            return new Dictionary<string, object>
            {
                ["count"] = rows.Count, ["total_area_m2"] = Math.Round(rows.Sum(r => Convert.ToDouble(r[4])), 3), ["columns"] = header,
                ["rows"] = rows.Take(limit).Cast<object>().ToList(), ["csv"] = csv,
            };
        }

        // ---------------------------------------------------------------- schedule roundtrip

        static Parameter FieldParam(Document doc, Element e, ScheduleField f)
        {
            ElementId pid = f.ParameterId;
            if (!Id.Valid(pid)) return null;
            Func<Element, Parameter> get = x =>
            {
                if (x == null) return null;
                if (Id.V(pid) < 0) return x.get_Parameter((BuiltInParameter)(int)Id.V(pid));
                return x.Parameters.Cast<Parameter>().FirstOrDefault(p => Id.V(p.Id) == Id.V(pid));
            };
            return get(e) ?? get(Rv.TypeElement(e));
        }

        static List<string> ParseCsvLine(string line, char sep)
        {
            var cells = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else cur.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == sep) { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            cells.Add(cur.ToString());
            return cells;
        }

        static object ScheduleRoundtrip(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string key = a.ReqStr("schedule");
            ViewSchedule vs = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase) || (long.TryParse(key, out long v) && Id.V(s.Id) == v))
                ?? throw new ToolError($"No schedule '{key}'.");
            ScheduleDefinition def = vs.Definition;
            var fields = Enumerable.Range(0, def.GetFieldCount()).Select(def.GetField).Where(f => !f.IsCalculatedField && Id.Valid(f.ParameterId)).ToList();
            string sep = a.Str("delimiter") ?? ",";
            string path = a.ReqStr("csv_path");
            List<Element> els = new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType().ToList();

            if (a.ReqStr("action").ToLowerInvariant() == "export")
            {
                var sb = new StringBuilder();
                sb.AppendLine(CsvLine(new object[] { "element_id" }.Concat(fields.Select(f => (object)f.GetName())), sep));
                foreach (Element e in els)
                    sb.AppendLine(CsvLine(new object[] { Id.V(e.Id) }.Concat(fields.Select(f => (object)(FieldParam(doc, e, f) is Parameter p ? Rv.SafeValueString(p) : ""))), sep));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                return new Dictionary<string, object> { ["schedule"] = vs.Name, ["rows"] = els.Count, ["columns"] = fields.Select(f => (object)f.GetName()).ToList(), ["csv"] = path };
            }

            if (!File.Exists(path)) throw new ToolError("CSV not found: " + path);
            string[] lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Length > 0).ToArray();
            if (lines.Length < 2) throw new ToolError("The CSV has no rows.");
            List<string> head = ParseCsvLine(lines[0].TrimStart('﻿'), sep[0]);
            int idCol = head.FindIndex(h => h.Trim().Equals("element_id", StringComparison.OrdinalIgnoreCase));
            if (idCol < 0) throw new ToolError("The CSV needs an element_id column (export it with action=export first).");
            bool dry = a.Bool("dry_run", false);
            var changes = new List<object>();
            int set = 0, failed = 0;
            for (int li = 1; li < lines.Length; li++)
            {
                List<string> cells = ParseCsvLine(lines[li], sep[0]);
                if (idCol >= cells.Count || !long.TryParse(cells[idCol], out long idv)) continue;
                Element e = doc.GetElement(Id.Make(idv));
                if (e == null) continue;
                for (int ci = 0; ci < head.Count && ci < cells.Count; ci++)
                {
                    if (ci == idCol) continue;
                    ScheduleField f = fields.FirstOrDefault(x => string.Equals(x.GetName(), head[ci].Trim(), StringComparison.OrdinalIgnoreCase));
                    Parameter p = f == null ? null : FieldParam(doc, e, f);
                    if (p == null || p.IsReadOnly) continue;
                    string now = Rv.SafeValueString(p) ?? "";
                    if (now == cells[ci]) continue;
                    var row = new Dictionary<string, object> { ["element_id"] = idv, ["field"] = f.GetName(), ["old"] = now, ["new"] = cells[ci] };
                    if (!dry)
                    {
                        try
                        {
                            if (p.StorageType == StorageType.Double) { if (!p.SetValueString(cells[ci])) throw new ToolError("Revit refused the value"); }
                            else Rv.SetParam(p, cells[ci]);
                            set++;
                        }
                        catch (Exception ex) when (ex is ToolError || ex is Autodesk.Revit.Exceptions.ApplicationException) { row["error"] = ex.Message; failed++; }
                    }
                    if (changes.Count < 200) changes.Add(row);
                }
            }
            return new Dictionary<string, object> { ["dry_run"] = dry, ["changes"] = changes.Count, ["set"] = set, ["failed"] = failed, ["rows"] = changes };
        }
    }
}
