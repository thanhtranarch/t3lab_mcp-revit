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

Sau khi cài: **mở lại Revit** (tab **T3Lab MCP** xuất hiện, server tự chạy) và **mở lại AI app**.
Revit 2026+ có thể hỏi về add-in chưa ký số → chọn **Always Load**.

```powershell
# Kiểm tra (không thay đổi gì): add-in từng năm, Python, AI app nào đã kết nối, Revit có đang trả lời không
& "$env:APPDATA\T3LabRevitMCP\install.ps1" -Check
# Chỉ kết nối lại AI app (hoặc nút T3Lab MCP > Connect AI trong Revit)
& "$env:APPDATA\T3LabRevitMCP\install.ps1" -ConnectOnly
# Gỡ hết
& "$env:APPDATA\T3LabRevitMCP\install.ps1" -Uninstall
```

Tuỳ chọn: `-Clients claude-desktop,claude-code` (chỉ vài app) · `-Years 2024,2026` (chỉ vài bản Revit) ·
`-Source <zip>` (cài offline) · `-Yes` (không hỏi; cho phép đóng & mở lại Claude Desktop).

> Claude Desktop ghi đè file config khi thoát, nên nếu nó đang mở: installer hỏi để đóng/mở lại, hoặc tự thêm server
> ngay khi bạn thoát Claude Desktop.

## Ribbon "T3Lab MCP"

| Nút | Việc |
|---|---|
| **Start / Stop** | Bật/tắt server MCP (tắt thì lần mở Revit sau cũng không tự chạy, tới khi bấm Start) |
| **Agent Cursors** | Bật/tắt thanh theo dõi AI |
| **Connect AI** | Thêm Revit vào mọi AI app trên máy (chạy `install.ps1 -ConnectOnly`) |
| **Status** · **Reload** | Port, số lần gọi, thư mục log · nạp lại `T3LabRevit.Core.dll` sau khi cập nhật, không cần restart Revit |

## Agent Cursors

- **Thanh (bar)**: mỗi AI app một dòng màu — trạng thái (Working 8 s / Thinking / Idle), mục tiêu và danh sách bước
  (AI gọi `t3lab_narrate`), việc đang chạy, 6 bước gần nhất (ok / failed / paused, số element thay đổi).
- **Pause**: mọi tool thay đổi model hoặc ghi file bị từ chối tới khi bấm Resume; tool đọc vẫn chạy.
- **Show on the model**: con trỏ của từng AI lướt tới element nó đọc/tạo/sửa trên mọi view đang mở, khung màu theo
  loại việc (xanh lá = tạo, cam = sửa, đỏ đứt nét = xoá); **Follow** zoom Revit theo AI.
- **Select changes**: chọn (và zoom) mọi element AI đã tạo/sửa trong model này. Thay đổi lấy từ sự kiện
  `DocumentChanged` của Revit, nên đúng cả khi tool không trả id.

## Tool (34)

Đơn vị trong mọi tool: **mm, m², m³, độ**; điểm `[x, y]` / `[x, y, z]` mm; element theo id số nguyên. Mỗi thay đổi =
**1 bước Undo**; warning của Revit được bỏ qua và báo lại (`revit_warnings`), error thì rollback.

| Nhóm | Tool |
|---|---|
| Trạng thái | `get_revit_context`, `model_overview` (audit nhanh: số element theo category, warning, family in-place, view chưa đặt lên sheet, room lỗi, link, CAD import, workset), `get_warnings`, `reload_addin` |
| File | `manage_document` (open · activate · save · save_as · close · sync with central · new) |
| Đọc | `query_elements` (lọc category / class / view / selection / level / type / tên / điều kiện parameter, `group_by`, phân trang), `get_element_info` (mọi parameter, location, bbox, host, workset, phase), `list_types`, `list_datums`, `get_selection` |
| View | `list_views`, `set_active_view`, `create_view` (plan · ceiling · structural · 3D · drafting · duplicate), `view_graphics` (tô màu · ẩn · isolate), `set_selection` (chọn + zoom), `export_image` (AI "nhìn" view) |
| Sửa | `set_parameter` (hàng loạt, instance hoặc type, nhận "1.2 m"), `transform_elements` (move · copy nhiều bản · rotate · mirror), `delete_elements` (`dry_run`) |
| Tạo | `create_walls`, `create_floor` (sàn / trần, từ điểm hoặc từ room), `create_levels`, `create_grids`, `place_family_instance` (cả cửa đi/cửa sổ tự tìm tường), `create_rooms`, `load_family` |
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
| AI báo *Revit … is not reachable* | Mở Revit; tab **T3Lab MCP → Status** phải báo *listening on 127.0.0.1:489xx*. Không thấy tab → chạy `-Check` |
| Không thấy tab T3Lab MCP | Revit 2026+: lúc mở Revit chọn **Always Load**. Kiểm tra `%APPDATA%\Autodesk\Revit\Addins\<năm>\T3LabRevit.addin` |
| AI app không thấy server | Thoát hẳn AI app rồi mở lại (Claude Desktop: tray icon → Quit). Chạy `-Check` để xem app nào đã đăng ký |
| Tool trả *Revit did not respond within 60s* | Revit đang mở dialog / đang ở chế độ edit (sketch, in-place) → đóng nó rồi thử lại. Không có gì bị thực thi |
| Cần log | `%APPDATA%\T3LabRevitMCP\logs\t3labrevit.log` (nút **Status → Open the log folder**) |

> **Trạng thái:** build 6 năm Revit (0 warning), test bridge và test đăng ký AI app đã chạy được (không cần Revit);
> job cài thật trên Windows chạy trong CI. **Chưa QA bên trong Revit thật** (con trỏ Agent Cursors, tool tạo/sửa).
> Báo lỗi ở Issues kèm file log.

MIT License · © T3Lab
