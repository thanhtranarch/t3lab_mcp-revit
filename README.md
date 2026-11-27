# T3Lab Revit MCP — add-in Revit cho Claude & mọi AI app

Add-in Revit (2022 → 2027) cho phép **Claude Desktop, Claude Code, ChatGPT (Codex), Cursor, VS Code Copilot,
Gemini CLI, Antigravity, Windsurf** đọc và sửa **model đang mở trong Revit** qua MCP — kèm **Agent Cursors**:
thanh theo dõi + con trỏ trên view cho thấy AI đang làm gì, và nút **Pause** để chặn mọi thay đổi.

Không cần pyRevit, không cần quyền admin, không cần cài .NET SDK. Cùng dòng với
[T3Lab CAD MCP](https://github.com/thanhtranarch/t3lab_CADmcp) (AutoCAD) và dựa trên MCP + Agent Cursors của
[t3lab-revit-api](https://github.com/thanhtranarch/t3lab-revit-api).

```
AI app ──stdio MCP──> bridge.py (%APPDATA%\T3LabRevitMCP) ──TCP 127.0.0.1:48910-48918──> T3LabRevit add-in trong Revit
```

## Cài đặt — chọn 1 trong 3 cách

### 1 · Một dòng lệnh (khuyến nghị)

Phím Windows → gõ `PowerShell` → Enter → dán dòng này → Enter:

```powershell
irm https://raw.githubusercontent.com/thanhtranarch/t3lab_revitmcp/main/install.ps1 | iex
```

### 2 · Bằng file

1. Tải **`T3LabRevitMCP.zip`** ở [Releases mới nhất](https://github.com/thanhtranarch/t3lab_revitmcp/releases/latest).
2. Chuột phải → **Extract All**.
3. Double-click **`Install.cmd`**.

### 3 · Đưa link GitHub cho AI tự cài

Dán vào Claude Code / Codex / Cursor / Gemini CLI (bất kỳ AI nào có terminal trên máy Windows của bạn):

> Cài và kết nối Revit với AI giúp tôi: https://github.com/thanhtranarch/t3lab_revitmcp — làm theo INSTALL_FOR_AI.md

AI sẽ đọc [`INSTALL_FOR_AI.md`](INSTALL_FOR_AI.md), chạy installer, kiểm tra bằng `-Check` rồi gọi thử
`get_revit_context`. Nếu bạn đang chat trên trình duyệt (claude.ai, chatgpt.com), AI không chạy được lệnh trên máy bạn
— nó sẽ đưa bạn dòng lệnh của cách 1.

### Installer làm gì

| Bước | Kết quả |
|---|---|
| Add-in | `%APPDATA%\T3LabRevitMCP\addin\<năm>\` + manifest `%APPDATA%\Autodesk\Revit\Addins\<năm>\T3LabRevit.addin` cho mọi Revit tìm thấy |
| Bridge | `%APPDATA%\T3LabRevitMCP\bridge.py` (chỉ dùng thư viện chuẩn Python) |
| Python | Dùng Python 3.8+ có sẵn; không có thì tải bản embeddable 3.12 (~11 MB) vào `%APPDATA%\T3LabRevitMCP\python` |
| AI app | Thêm server **`t3lab-revit-mcp`** vào mọi app tìm thấy (backup file config trước, giữ nguyên server khác) |

Sau khi cài: **mở lại Revit** (panel **T3Lab MCP** xuất hiện trong tab **Add-Ins**, server tự chạy) và **mở lại AI app**.
Revit 2026+ có thể hỏi về add-in chưa ký số → chọn **Always Load**.

```powershell
# Kiểm tra (không thay đổi gì): add-in từng năm, Python, AI app nào đã kết nối, Revit có đang trả lời không
& "$env:APPDATA\T3LabRevitMCP\install.ps1" -Check
# Chỉ kết nối lại AI app (hoặc nút Add-Ins > T3Lab MCP > Connect AI trong Revit)
& "$env:APPDATA\T3LabRevitMCP\install.ps1" -ConnectOnly
# Gỡ hết
& "$env:APPDATA\T3LabRevitMCP\install.ps1" -Uninstall
```

Tuỳ chọn: `-Clients claude-desktop,claude-code` (chỉ vài app) · `-Years 2024,2026` (chỉ vài bản Revit) ·
`-Source <zip>` (cài offline) · `-Yes` (không hỏi; cho phép đóng & mở lại Claude Desktop).

> Claude Desktop ghi đè file config khi thoát, nên nếu nó đang mở: installer hỏi để đóng/mở lại, hoặc tự thêm server
> ngay khi bạn thoát Claude Desktop.

## Ribbon: Add-Ins › T3Lab MCP

Add-in không tạo tab riêng: các nút nằm trong panel **T3Lab MCP** của tab **Add-Ins** có sẵn trong Revit. Cùng nút,
thứ tự, icon (T3 Space Line của t3lab-revit-api, theo theme sáng / tối của Revit 2024+) và câu chữ với
[T3Lab CAD MCP](https://github.com/thanhtranarch/t3lab_mcp-cad) trên tab Add-ins của AutoCAD.

| Nút | Việc |
|---|---|
| **Start / Stop** | Bật/tắt server MCP (tắt thì lần mở Revit sau cũng không tự chạy, tới khi bấm Start) |
| **Agent Cursors** | Bật/tắt thanh theo dõi AI (server đang tắt thì bật luôn server) |
| **Connect AI** | Thêm Revit vào mọi AI app trên máy, hoặc chỉ kiểm tra (chạy `install.ps1 -ConnectOnly` / `-Check`) |
| **Status** · **Reload** | Port, tool, số lần gọi, hàng đợi, Agent Cursors, log · nạp lại `T3LabRevit.Core.dll` sau khi cập nhật, không cần restart Revit |

## Agent Cursors

- **Thanh (bar)**: giống hệt bảng của T3Lab CAD MCP (cùng bố cục, chữ, màu; sáng / tối theo theme Revit). Mỗi AI app:
  con trỏ màu, tên, trạng thái, số lần gọi · số thay đổi, mục tiêu, kế hoạch *Step 2/5* + thanh tiến độ + ✓ ▸ ○ (AI gọi
  `t3lab_narrate`), dải màu của việc đang chạy; rồi **RECENT**: 6 bước gần nhất (✓ ⏸ – ✕, "Claude · 12 s ago", số thay
  đổi · giây hoặc lỗi). Trạng thái:
  *In line · 1 ahead* (đang xếp hàng sau AI khác, dải vàng ghi "after Claude Code") · *Waiting for Revit* (tới lượt nhưng
  Revit đang bận dialog / edit mode) · *Working 8 s* · *Thinking ···* (vừa xong một bước, đang chờ model, 20 s) ·
  *Idle · last step 3 min ago*.
- **Mỗi chat một agent**: hai cửa sổ Claude Code là "Claude Code" và "Claude Code 2", hai màu, hai con trỏ.
- **Nút** (cùng thứ tự với AutoCAD): **Pause / Resume** (mọi tool thay đổi model hoặc ghi file bị từ chối; tool đọc vẫn
  chạy) · **Select changes** · **Replay** (chiếu lại các thay đổi gần nhất trong model, từng bước, với con trỏ) · **Clear**.
- **Show on model** (mặc định bật): con trỏ của từng AI lướt tới element nó đọc/tạo/sửa trên mọi view đang mở, khung màu
  theo loại việc (xanh lá = tạo, xanh dương = sửa, đỏ đứt nét = xoá, xám = đọc); **Follow** zoom Revit theo AI.
  - Tag con trỏ nền pastel + chữ đậm màu (dễ đọc trên view trắng), ghi **thứ vừa làm**: "Claude · Wall 3450 × 200",
    "Door 0915 x 2134mm", "Wall ×3"; trong lúc chạy ghi việc đang làm, giữa hai bước ghi "thinking ···".
  - **Vẽ theo (draw-along)**: tường, trục, dầm, line mới tạo → con trỏ chạy dọc từ đầu tới cuối từng cái trên view
    đang mở, kèm một vệt màu.
  - Hai AI cùng trỏ một chỗ thì đứng lệch nhau, không đè lên nhau.
- **Select changes**: chọn (và zoom) mọi element AI đã tạo/sửa trong model này. Thay đổi lấy từ sự kiện
  `DocumentChanged` của Revit, nên đúng cả khi tool không trả id.

## Nhiều AI cùng lúc · tuỳ chọn bridge

Revit chỉ chạy một lời gọi một lúc. Add-in xếp hàng công bằng (`Conductor.cs`): người đang chat trước bot, rồi xoay vòng
theo từng agent (một agent gọi dồn dập không chặn agent khác). Lời gọi chờ quá hạn (`queue_wait_s`, mặc định 45 s) thì
**không chạy** và trả về `{"error_code": "revit_busy", "nothing_ran": true, "retry_after_s": …, "running": {...}}` — không
còn chuyện "ghi ma" (model đổi sau khi AI đã bỏ cuộc). `health` / nút Status cho thấy ai đang giữ Revit và bao nhiêu
lời gọi đang chờ.

Tuỳ chọn thêm vào `args` của server `t3lab-revit` trong config AI app (hoặc biến môi trường):

| Tuỳ chọn | Biến | Việc |
|---|---|---|
| `--tier=read` · `trusted` · `experimental` | `T3LAB_TIER` | `read`: chỉ tool đọc · `trusted`: thêm tool sửa model / ghi file · `experimental` (mặc định): thêm `send_code_to_revit`. Tool vượt tier bị ẩn và bị từ chối (`tier_denied`) trước khi tới Revit |
| `--deny=a,b` | `T3LAB_DENY` | Tắt vài tool cho client này (`tool_denied`) |
| `--agent=<tên>` | `T3LAB_AGENT` | Tên hiện trên Agent Cursors (mặc định: tên app) |
| `--lane=person` · `task` · `auto` | `T3LAB_LANE` | Thứ tự hàng đợi (bot tự chạy: `auto`). Chỉ xếp thứ tự, không cấp quyền |
| `--queue-wait=45` | `T3LAB_QUEUE_WAIT_S` | Thời gian chờ tối đa (giây) để tới lượt |
| `--audit=0` | `T3LAB_AUDIT=0` | Tắt nhật ký `%APPDATA%\T3LabRevitMCP\audit\<ngày>.jsonl` (mỗi lời gọi một dòng: agent, tool, ok / error_code, thời gian; không ghi tham số) |

## Tool (63)

Đơn vị trong mọi tool: **mm, m², m³, độ**; điểm `[x, y]` / `[x, y, z]` mm; element theo id số nguyên. Mỗi thay đổi =
**1 bước Undo**; warning của Revit được bỏ qua và báo lại (`revit_warnings`), error thì rollback.

| Nhóm | Tool |
|---|---|
| Trạng thái | `get_revit_context`, `model_overview` (audit nhanh: số element theo category, warning, family in-place, view chưa đặt lên sheet, room lỗi, link, CAD import, workset), `get_warnings`, `reload_addin` |
| File | `manage_document` (open · activate · save · save_as · close · sync with central · new) |
| Đọc | `query_elements` (lọc category / class / view / selection / level / type / tên / điều kiện parameter, `group_by`, phân trang), `get_element_info` (mọi parameter, location, bbox, host, workset, phase), `list_types`, `list_datums`, `get_selection` |
| View | `list_views`, `set_active_view`, `create_view` (plan · ceiling · structural · 3D · drafting · duplicate), `view_graphics` (tô màu · ẩn · isolate), `set_selection` (chọn + zoom), `export_image` (AI "nhìn" view) |
| **Python** | **`send_code_to_revit`**: AI tự viết code IronPython 3.4 (cú pháp Python 3, có `json`, `re`, `collections`…) với toàn bộ Revit API cho việc không có tool riêng. Mặc định chạy trong 1 transaction = 1 bước Undo; lỗi thì trả traceback và rollback |
| Sửa | `set_parameter` (hàng loạt, instance hoặc type, nhận "1.2 m"), `transform_elements` (move · copy nhiều bản · rotate · mirror), `delete_elements` (`dry_run`), `rename_elements` (find/replace, regex, prefix/suffix, số sheet), `rehost_to_level` (đổi level giữ nguyên cao độ), `join_geometry` (join · unjoin · switch · check), `split_element` (tường, dầm, ống, duct), `edit_text_notes` (find/replace), `purge_unused` (`dry_run`) |
| Tạo | `create_walls`, `create_floor` (sàn / trần, từ điểm hoặc từ room), `create_levels`, `create_grids`, `place_family_instance` (cả cửa đi/cửa sổ tự tìm tường), `place_line_based` (dầm, giằng), `create_rooms`, `load_family`, `tag_all_in_view` (tag mọi element chưa có tag) |
| Quản lý model | `list_worksets` / `manage_worksets` / `set_element_workset`, `list_links` / `manage_links` (link .rvt / CAD, load · unload · reload · pin), `manage_groups`, `list_materials` / `manage_material`, `manage_revisions`, `list_project_parameters` / `create_project_parameter`, `project_storage` (lưu ghi chú trong model), `transfer_elements` (copy type / element từ model khác đang mở) |
| View template / filter | `manage_view_templates` (list · apply · apply_once · remove · tạo từ view · chuyển từ model khác), `list_view_filters`, `create_view_filter` (rule + màu + ẩn/hiện), `manage_view` (scale, detail level, visual style, crop box, section box) |
| Khối lượng / dữ liệu | `get_material_quantities` (m², m³ theo vật liệu), `export_room_data` (+ CSV), `schedule_roundtrip` (schedule → CSV → sửa trong Excel → nạp lại) |
| Ghi chú | `create_annotation` (text · detail line · tag · dimension qua grid/tường) |
| Sheet / schedule | `list_sheets`, `create_sheets`, `place_views_on_sheet`, `get_schedule_data` (+ CSV cho Excel), `create_schedule` |
| Xuất | `export_documents` (PDF gộp/tách · DWG · IFC) |
| Agent Cursors | `t3lab_narrate` (mục tiêu + các bước hiện trên thanh) |
| Bridge | `list_revit_instances`, `switch_revit_instance` (nhiều Revit cùng lúc; mỗi Revit 1 port 48910–48918) |

### Ví dụ yêu cầu

| Bạn nói | AI dùng |
|---|---|
| "Model đang mở có bao nhiêu tường, sàn, cửa theo từng tầng?" | `query_elements group_by=level` |
| "Kiểm tra sức khoẻ model này" | `model_overview` → `get_warnings` |
| "Đánh Mark cho mọi cửa tầng 2 theo thứ tự D201, D202…" | `query_elements` → `set_parameter changes=[...]` |
| "Vẽ phòng 6×4 m: tường 200, sàn, đặt room tên 'Phòng họp'" | `t3lab_narrate` → `create_walls closed` → `create_floor` → `create_rooms` |
| "Tạo sheet A101–A105 rồi đặt các mặt bằng lên" | `create_sheets` → `place_views_on_sheet` |
| "Xuất tất cả sheet ra 1 file PDF" | `export_documents format=pdf sheets=["*"]` |
| "Tô đỏ các cột không có Mark trong view này" | `query_elements where` → `view_graphics color` |

## Build từ mã nguồn

Cần .NET SDK 10 (build được cả net48, net8, net10; Revit API lấy từ NuGet `Nice3point.Revit.Api.*`, không cần cài Revit):

```bash
for y in 2022 2023 2024 2025 2026 2027; do
  dotnet build src/T3LabRevit.Core/T3LabRevit.Core.csproj   -c Release -p:RevitYear=$y
  dotnet build src/T3LabRevit.Loader/T3LabRevit.Loader.csproj -c Release -p:RevitYear=$y
done
python scripts/gen_tools_snapshot.py && python scripts/package.py      # -> dist/T3LabRevitMCP.zip
powershell -ExecutionPolicy Bypass -File install.ps1 -Source dist\T3LabRevitMCP.zip
```

Test (không cần Revit): `python -I tests/smoke_bridge.py` · `python -I tests/test_register.py` ·
`python -I scripts/gen_tools_snapshot.py --check`. CI (`.github/workflows/release.yml`) build 6 năm với
`-warnaserror`, chạy test, **cài thật trên Windows runner** rồi phát hành `T3LabRevitMCP.zip` mỗi lần merge vào `main`.

| Revit | .NET | Thư mục |
|---|---|---|
| 2022 · 2023 · 2024 | .NET Framework 4.8 | `addin/2022..2024` |
| 2025 · 2026 | .NET 8 | `addin/2025..2026` |
| 2027 | .NET 10 | `addin/2027` |

## Khi có lỗi

| Triệu chứng | Xử lý |
|---|---|
| AI báo *Revit … is not reachable* | Mở Revit; **Add-Ins → T3Lab MCP → Status** phải báo *listening on 127.0.0.1:489xx*. Không thấy panel → chạy `-Check` |
| Không thấy panel T3Lab MCP trong tab Add-Ins | Revit 2026+: lúc mở Revit chọn **Always Load**. Kiểm tra `%APPDATA%\Autodesk\Revit\Addins\<năm>\T3LabRevit.addin` |
| AI báo `revit_busy` | AI khác đang dùng Revit (xem `running`), hoặc Revit đang mở dialog / edit mode. Không có gì chạy; AI thử lại sau `retry_after_s` |
| AI app không thấy server | Thoát hẳn AI app rồi mở lại (Claude Desktop: tray icon → Quit). Chạy `-Check` để xem app nào đã đăng ký |
| Tool trả *Revit did not respond within 60s* | Revit đang mở dialog / đang ở chế độ edit (sketch, in-place) → đóng nó rồi thử lại. Không có gì bị thực thi |
| Cần log | `%APPDATA%\T3LabRevitMCP\logs\t3labrevit.log` (nút **Status → Open the log folder**) |

> **Trạng thái:** build 6 năm Revit (0 warning), test bridge và test đăng ký AI app đã chạy được (không cần Revit);
> job cài thật trên Windows chạy trong CI. **Chưa QA bên trong Revit thật** (con trỏ Agent Cursors, tool tạo/sửa).
> Báo lỗi ở Issues kèm file log.

MIT License · © T3Lab
