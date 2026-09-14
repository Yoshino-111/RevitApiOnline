# FamilyMEP - Tool Library

## Smart Tag: Standard - Near Host (Experimental)

### Một kết quả cho Analyze và Write

Bước kéo gần host giữ nguyên các tag Standard đã sạch và gần host, chia phần cần sửa thành các cụm host cục bộ. Với Side Auto, thử cả trái và phải, ưu tiên cột tag lân cận trong phạm vi gần host; không khóa theo phía Standard cũ. Các tag trong cụm dùng chung mép chữ trái như Standard, kể cả khi khác category hoặc độ dài chữ. Hàng được xếp theo thứ tự host từ trên xuống, chừa khoảng cách và kiểm tra text/model, text/text, leader/text, leader/leader trước khi nhận.

Các cụm cần sửa được giải chung: leader cũ của một cụm đang chờ dời không khóa khoảng trống của cụm khác. Khi hết tìm kiếm, mọi tag chưa giải được vẫn giữ vị trí Standard; cụm mới nào đụng các tag được giữ lại sẽ được hoàn tác cả cụm. Không bỏ tag hoặc bỏ qua clash để cho Write. Mỗi phương án cột có ngân sách riêng để một phía bị chắn không tiêu hết lượt tìm kiếm của cả view. Độ lệch điểm chèn family theo cả X/Y được dùng ngay trong solver, giúp tọa độ head và mép chữ khớp khi áp dụng lên tag thật.

Khoảng cách hàng Near Host dùng giá trị lớn hơn giữa **Tag Gap** và **Clearance**, để cụm xếp với Gap 1 mm không bị kiểm tra cuối loại vì Clearance 2 mm. Nếu dải ống/fitting chiếm hết vùng sát host, vẫn thử cột trống lân cận trong Group Search Width; tag đang ở xa phải được kéo gần đáng kể. Tag ngắn được chừa phần chênh chiều rộng để align chung mép trái với tag dài. Không giảm tiêu chuẩn chống clash.

Analyze Near Host lưu bản ghi hình học gần nhất tại `logs/smarttag-nearhost-latest.json` trong thư mục dữ liệu FamilyMEP. File chứa kích thước/tọa độ đã đo, obstacles, settings, Standard và kết quả Near Host; không lưu nội dung chữ tag hoặc đường dẫn model. Dùng biến `SMARTTAG_REPLAY` trỏ tới file này khi chạy `tests/SmartTagLayoutSmokeTest` để tái hiện solver không cần Revit và liệt kê từng cặp gây clash. Đây là dữ liệu chẩn đoán cục bộ, không tự gửi ra ngoài.

Nút ghi draft đã được gỡ. Analyze đo family thật và lưu kích thước để dùng chung cho Standard và Near Host, chỉ nhận cột gần host hơn khi sạch clash. Kết quả head/end/elbow và mép chữ sau Analyze được giữ lại; Write ghi đúng kết quả đó, không chạy lại solver/packing. Nếu model, family hoặc thiết lập đổi thì phải Analyze lại. Write kiểm tra Revit có giữ đúng hình học đã lưu trước khi commit; sai lệch sẽ hoàn tác. Chỉ bố cục đã kiểm tra đạt mới được Write.

Near Host chạy bước đo/xếp hàng và tối ưu leader thật của Standard trước khi sửa cục bộ. Không cho ghi bỏ qua clash: cả UI và service đều chặn Write nếu kiểm tra cuối còn lỗi; preview vẫn được giữ để kiểm tra.

Near Host dùng Standard làm nền rồi sửa cục bộ: khóa các tag đã sạch, thử lại cột/hàng/Free End cho cụm đang clash, sau đó thử từng tag còn kẹt. Chỉ nhận một cụm sửa khi không phát sinh text/model, text/text, leader/text hoặc leader/leader clash với phần còn lại. Hết ngân sách vẫn giữ các sửa thành công; không quay lại toàn bộ bố cục lỗi ban đầu. Kết quả cuối báo số tag còn cần xử lý, và Write vẫn kiểm tra lại với tag thật.

Near Host không hiển thị khung DTO mô phỏng. Trong lúc Analyze chỉ giữ ảnh model sạch; khi transaction tạm hoàn thành mới hiển thị ảnh tag family thật giống Standard. Phần tìm vị trí gần host giới hạn khoảng 4 giây/1.000.000 bước và giữ kết quả đầy đủ tốt nhất qua các lượt sửa cục bộ. Cạnh obstacle được lọc theo vùng host và gộp các tọa độ X gần trùng nhau trước khi thử cột khác. Không còn mốc hủy 30 giây cho bước tạo/đo tag thật. Standard giữ nguyên thuật toán.

**Giới hạn đã tái hiện trên model thực tế (08/09/2026):** bản ghi Analyze 10:51 gồm 138 tag có 111 clash kiểm chứng (15 text/model, 96 cặp annotation). Bộ đếm cũ hiển thị 186 do cộng từng đoạn giao nhau; Near Host nay dùng cùng bộ kiểm tra cho số hiển thị và điều kiện Write, không hiển thị `0 NEW` để bỏ qua lỗi Standard. Solver sửa cục bộ mới vẫn chưa giải sạch bộ dữ liệu này (lượt replay khoảng 4 giây còn 64 clash); chưa coi là bản sửa hoàn chỉnh và Write vẫn bị chặn khi còn clash. Test hình học tổng hợp không thay thế kiểm chứng này.

Tối ưu Analyze/Write: loại sớm phương án không thể tốt hơn kết quả hiện tại, lọc obstacle ngoài vùng đường đi và không đo lặp tag nhiều leader. Vẫn đo tag thật và kiểm tra clash trước khi ghi; Standard không đổi. Có thể chạy benchmark thuật toán bằng biến môi trường `SMARTTAG_BENCHMARK=1` với project `tests/SmartTagLayoutSmokeTest` (không bao gồm thời gian API Revit).

- Giữ nguyên thuật toán và lựa chọn mặc định **Standard**.
- Chọn **Layout Style → Standard - Near Host (Experimental)** để thử bản riêng, rồi Apply Selection + Analyze.
- Bản thử nghiệm chia cụm theo vị trí host gần nhau, căn chung mép chữ trong cụm và thử cột trống lân cận khi có clash. Không cố định số tag mỗi cụm.
- Kiểm tra text/model, text/text và leader/text hai chiều; leader ngang hoặc gấp vuông góc. Khi đo tag thật, dùng kích thước và độ lệch chữ của family dự án, đồng thời giữ chỗ cho tag hiện hữu không di chuyển.
- Near Host thử cột trống xa hơn khi cột gần bị chắn và thử các đường Free End song song trên host. Analyze vẫn hiển thị tag thật khi còn clash (transaction preview luôn hoàn tác); Write không bỏ qua lỗi. Chưa tự thay thế Standard.

## Ba loại thư viện

- **Tool Library**: thư viện editable mặc định. `From Project` tự lưu family vào đây, Sync category và tạo cache 3D.
- **Built-in Library (`.familymeplib`)**: thư viện đóng băng, chỉ đọc, dùng để phát hành cùng tool.
- **Package (`.familymeppack.zip`)**: file trao đổi thủ công, độc lập với Tool Library.

## Tạo thư viện mặc định

1. Mở `Packages > Save families to Tool Library...` nếu family đang nằm ở thư mục ngoài.
2. Family lấy bằng `From Project` đã được tự lưu vào Tool Library.
3. Chạy Sync/Cache nếu thư viện cũ còn thiếu dữ liệu.
4. Chọn `Packages > Freeze as Built-in Library...`.
5. Chọn category/family và lưu file `.familymeplib` trong thư mục được tool mở sẵn.

Built-in Library chứa RFA, category metadata và preview 3D. Khi mở Browser, tool chỉ đọc chỉ mục và preview. RFA chỉ được giải nén vào cache trên F: khi Open/Load hoặc một chức năng cần đọc file thật.

## Tạo bản phát hành cho máy khác

Chạy:

```powershell
.\publish-portable.ps1
```

Script tạo một thư mục release mới trong `F:\FamilyMEPReleases`. Chép nguyên thư mục release sang ổ F: của máy nhận và chạy `INSTALL.cmd`.

Người nhận không cần Add Folder, Import Package, Sync category hoặc Cache All lại. Chỉ file manifest `.addin` nhỏ được đăng ký trong AppData của Windows user; DLL, family, cache và dữ liệu vẫn ở ổ F:.

## Cập nhật family

Family mới được lưu trong `Data\tool-library\user-library`. Kho Built-in không bị sửa trực tiếp. Khi đã kiểm tra xong thư viện mới, chạy `Freeze as Built-in Library` để tạo phiên bản đóng băng mới.
