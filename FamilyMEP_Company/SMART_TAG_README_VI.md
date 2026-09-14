# Smart Tag Setup — Revit 2025

Tool tự tạo các `IndependentTag` MEP còn thiếu, bố trí lại tag đang có trong Active View và cho xem trước trong WPF trước khi ghi vào model.

## Workflow

> Active View capture chi lay dung rectangle cua `UIView`. WPF duoc an va Revit duoc dua len truoc tam thoi bang `NO MOVE + NO SIZE`; tool khong restore, maximize, move hoac resize Revit. Neu Revit dang minimized, nguoi dung phai tu restore truoc khi Refresh.

1. Mở Floor Plan, RCP, Section hoặc Elevation cần xử lý.
2. Chọn **FamilyMEP > Annotation Tools > Smart Tag**.
3. Tool ẩn cửa sổ trong thời gian ngắn để chụp đúng viewport hiện tại.
4. Tool chỉ liệt kê các category MEP thực sự đang nhìn thấy trong vùng viewport hiện tại. Ban đầu không category nào được chọn.
5. Chọn một hoặc nhiều category cần tag và `Tag Family : Type`. Bước này chỉ tạo selection set; tool chưa chạy solver, chưa sắp tag và chưa thay đổi Revit.
6. Nhập search width, short offset, row spacing, view margin và clearance. Các giá trị mm là kích thước trên giấy và được quy đổi theo View Scale.
7. Kiểm tra Project Tag Preview:
   - `[NEW]`: tag sẽ được tạo khi Apply;
   - xanh lá: leader ngang một nét;
   - cam: tag bị dời dùng Free End; endpoint chỉ trượt rất nhỏ bên trong host và có một elbow 90°;
   - đỏ: clash chưa giải quyết; đây là cảnh báo nhưng không khóa nút ghi để có thể test trực tiếp trong Revit.
8. Mỗi category có combobox chọn `Tag Family : Type` đang load trong project. Type đang được dùng nhiều nhất được ưu tiên mặc định.
9. Sau khi chọn đủ 1–3 category, bấm **Apply Selection + Analyze**. Lúc này toàn bộ category đã chọn mới được đưa vào cùng một bài toán; tool đo family thật, quyết định thứ tự trên–dưới, chuẩn hóa rail, tách text và tối ưu leader trong Transaction Group rollback.
10. Chỉ sau khi Analyze hoàn thành, nút **Write Analyzed Tags to Revit** mới được bật để tạo tag thiếu và ghi đúng kết quả đã phân tích vào Revit.

## Rule bố trí

- Sau **Apply Selection + Analyze**, toàn bộ tag được khóa vào đúng hai rail toàn cục. Cạnh trái của family tag thật dùng chung một tọa độ X ở mỗi bên, nên tag có độ rộng khác nhau vẫn nhìn thẳng hàng.
- Khi bật/tắt category, toàn bộ category đang chọn được tính lại cùng nhau. Auto Side chia host theo vị trí trái/phải so với tâm nhóm; host trùng trục được cân bằng sang hai phía.
- Solver chỉ được đổi hàng theo phương đứng hoặc đổi leader sang Free End để tránh clash; không được tạo thêm rail X làm tag lòi ra ngoài hai cột chuẩn.
- Trong từng cụm dọc, Analyze dùng một pitch hàng cố định lấy theo Tag Family thật cao nhất cộng Row Spacing. Khoảng trống lớn chia thành cụm riêng; tag đơn lẻ giữ đúng cao độ host để đi thẳng.
- Sau khi khóa hai cột và lưới hàng, Analyze chạy thêm tối đa ba lượt tối ưu Free End trên đúng bounding box host. Leader sạch được giữ nguyên; route còn clash chỉ dịch đầu mút ngang/dọc một khoảng nhỏ trong host, không phá lại hàng Y hoặc tọa độ X đã căn.
- Mỗi lượt tối ưu dựng lại conflict graph của toàn bộ leader. Route đang cắt nhiều hàng/leader nhất được xử lý trước; khi cùng mức xung đột, leader thẳng được ưu tiên trước leader có elbow, tiếp theo là leader có đoạn đứng dài hơn. Vì vậy thứ tự Analyze thích ứng sau mỗi thay đổi đầu mút thay vì luôn chạy đơn thuần từ trên xuống dưới.
- Tag được ưu tiên theo tọa độ element từ trên xuống dưới trong vùng có khả năng va chạm.
- Một cụm tag dày được cân bằng quanh cao độ các host: một phần dịch lên và một phần dịch xuống, vẫn giữ thứ tự trên–dưới. Khoảng dịch dọc bị giới hạn theo chiều cao tag nên tag gần không bị kéo xuống cuối view.
- Với cặp A/B chồng text, B phía trên được giữ đúng một leader ngang; chỉ A phía dưới dịch xuống và nhận một elbow vuông góc. Tag tách biệt như C vẫn giữ leader thẳng.
- Free-End lane được cấp và reset theo từng collision block cục bộ, không kéo lane từ cụm trên xuyên qua cụm dưới.
- Nếu đủ khoảng trống, leader là một đường ngang thẳng.
- Tag không clash dùng một Free endpoint đúng tại anchor host và collapse elbow tại TagHead, vì vậy chỉ còn đúng một line ngang. Tag bị chồng được dời hàng; endpoint chỉ trượt nhẹ trong bounding box host. Elbow có cùng trục X với endpoint để tạo đúng một đoạn dọc + một đoạn ngang.
- Khi ghi vào Revit, tool đo bounding box thật của family tag khi chưa có leader rồi hiệu chỉnh theo phương ngang. Vì vậy cạnh trái của các family/tag type khác nhau bám đúng rail như trên preview.
- Sau khi đo family thật, tool chạy thêm một pass đóng gói hàng theo đúng chiều cao tag thực tế. Tag dưới bị chồng sẽ ưu tiên dịch xuống trong phạm vi gần host; nếu phải dịch thì chuyển sang Free End với một elbow vuông góc. Kết quả Actual Clashes trên WPF được tính từ pass thật này, không lấy từ hộp text sample.
- Analyze dùng thứ tự ưu tiên cứng: tách text tag trước, sau đó align cạnh trái của các rail gần nhau, cuối cùng mới giảm leader–leader và leader–element clash. Free End thử nhiều endpoint lane nhỏ bên trong bounding box host để tách các đoạn đứng trùng nhau mà vẫn giữ một elbow 90°.
- Trong từng rail, Analyze khóa thứ tự tag theo cao độ host từ trên xuống dưới; host gần cùng cao độ ưu tiên leader ngắn hơn. Quy tắc đơn điệu này ngăn tag của host dưới nhảy lên trên tag của host trên, là nguyên nhân chính tạo các đường leader cắt nhau. WPF báo riêng số `Text Overlaps` và `Leader Crossings` sau tối ưu.
- Mỗi tag được thử lại tại đúng cao độ anchor host trước. Nếu tuyến ngang không va chạm, tool ghi endpoint chính xác tại anchor và collapse elbow để line đi thẳng; chỉ khi tuyến thẳng bị chặn mới dịch row và dùng một elbow. Endpoint bắt tại cạnh bounding box host gần phía tag nhất rồi thử các lane nhỏ hướng vào trong host.
- Hàm chọn route tính cả chi phí di chuyển row. Một tuyến Attached ngắn sẽ không còn bị thay bằng elbow dài chỉ để né một va chạm element có mức độ thấp; text chồng và leader giao cắt vẫn có trọng số lớn hơn. Các tag thẳng trở thành mốc, các host gần phía trên/dưới được xếp quanh các mốc này theo đúng thứ tự.
- Mỗi rail được chia tiếp thành các block dọc cục bộ dựa trên khoảng cách giữa host và tổng span của nhóm. Thứ tự trên–dưới chỉ khóa bên trong block; sang nhóm xa thì reset. Khoảng dịch khỏi anchor có giới hạn cứng, nên tag như A không được kéo xuống một nhóm annotation ở xa chỉ để đạt điểm clash thấp hơn.
- Sau khi Create + Apply, tool không tự chụp và giải lại toàn view. Người dùng kiểm tra kết quả trong Revit rồi bấm Refresh Active View khi muốn chạy vòng tiếp theo.
- Tool kiểm tra va chạm với MEP element, leader khác và text tag theo các checkbox trong UI.
- Trong WPF, dùng con lăn chuột để zoom 100–800% quanh đúng vị trí con trỏ.
- Tag bị pinned, orphaned hoặc không có local tagged reference sẽ bị bỏ qua.

## Phạm vi bản đầu

- Tự tạo một tag theo category cho mỗi element chưa có tag; element đã có tag chỉ được sắp xếp lại, không tạo trùng.
- Revit phải có Tag Family tương thích đã được load cho category. Element thiếu family sẽ được bỏ qua và hiển thị cảnh báo sau khi Apply.
- Hỗ trợ Duct, Pipe, Pipe Accessory/Valve, Mechanical Equipment, Air Terminal và Sprinkler.
- Preview dùng ảnh chụp Active View, nhưng tính toán dùng bounding box và tọa độ Revit thật, không nhận dạng ảnh.
- Ribbon loader phải được build/deploy khi Revit đóng. DLL nghiệp vụ có thể hot-reload bằng `build-plugin.ps1`.

## Build và kiểm tra

```powershell
.\build-plugin.ps1

$dotnet = 'dotnet'
& $dotnet build .\RevitHotLoader2025\RevitHotLoader2025.csproj `
  --configuration Release --output .\.verify\loader
& $dotnet run --project .\tests\SmartTagLayoutSmokeTest\SmartTagLayoutSmokeTest.csproj `
  --configuration Release
```

Khi Revit đang mở, `deploy\FamilyMEP.Loader.dll` bị khóa. Đóng Revit rồi chạy `build-all.ps1` để cập nhật ribbon **Smart Tag**.

## Zero-clash analysis

- Chỉ số `Clashes` chỉ đếm xung đột trực quan cần sửa: text chồng text, leader cắt leader hoặc leader xuyên qua text tag. Leader chạm vùng MEP gần chính host không còn làm tăng sai bộ đếm này.
- Solver so sánh số critical clash trước mọi chi phí khác. Route có 0 clash luôn được ưu tiên trước route ngắn nhưng còn giao cắt.
- Khi một hàng đã kín, solver thử các hàng Y gần đó trên chính rail hiện tại. Nếu vẫn không đủ chỗ, WPF báo clash để người dùng kiểm tra; solver không đẩy tag sang một rail X phụ.
- Các family tag khác độ rộng align theo cạnh gần model: tag bên phải dùng cạnh trái (`MinU`), tag bên trái dùng cạnh phải (`MaxU`). End, elbow và TagHead được dựng từ cùng một view-plane origin nên route thẳng luôn ngang và route một elbow chỉ gồm đúng một đoạn ngang cộng một đoạn dọc.
- Sample center-group workflow: ở chế độ Auto Left + Right, host được chia theo vị trí so với tâm toàn bộ nhóm và tất cả tag của mỗi phía cùng bám một cạnh text chuẩn. Tag vẫn được xếp theo cao độ host từ trên xuống dưới. Solver thử leader thẳng tại đúng cao độ host trước; nếu clash mới dời tag sang hàng Y gần nhất và dùng Free End với đúng một elbow 90°. Hai host gần trùng cao độ giữ một tag thẳng và dời tag còn lại một row nhỏ. Sau khi bật leader thật, tool regenerate và ghi lại route lần hai để loại bỏ đường xiên tạm thời do attachment point của Tag Family thay đổi.
- Mục tiêu là `0 Clashes`. Nếu hai rail cố định và các hàng Y hợp lệ vẫn không đủ chỗ, WPF báo đúng số giao cắt còn lại thay vì âm thầm tạo cột X thứ ba hoặc báo cả những tiếp xúc MEP không thể tránh.
