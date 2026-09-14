# Smart Dim — Revit 2025

Smart Dim nằm trên thanh trên cùng của cửa sổ **Smart Tag Setup**. Mục layout **Standard - Near Host** đã được gỡ khỏi danh sách.

## Cách chạy

1. Có thể chọn sẵn element trong Revit, hoặc để trống selection.
2. Mở **FamilyMEP > Annotation Tools > Smart Tag**.
3. Bấm **SMART DIM**. Nếu chưa chọn sẵn element, kéo một khung chọn trong Revit.
4. Tool ghi `Dimension` thật trực tiếp vào active plan view.

## Quy tắc hiện tại

- Tâm thiết bị/miệng gió cùng hàng hoặc cùng cột được gom thành một chuỗi dim thẳng hàng; chuỗi nối đến Grid hoặc mặt Wall gần nhất khi có reference phù hợp. Wall/Grid trong Revit Link cũng được xét.
- Mechanical, Plumbing và Electrical Equipment: ngoài chuỗi tâm còn dim kích thước ngang/dọc bằng reference plane thật của family.
- Air Terminal chỉ dim tâm theo chuỗi, không lặp kích thước trên từng miệng gió.
- Duct Accessory không bị dim chi tiết.
- Toàn bộ duct thẳng chữ nhật được gom theo vùng thành chuỗi cạnh: tuyến ngang dùng chuỗi dim đứng, tuyến đứng dùng chuỗi dim ngang. Mỗi chuỗi thể hiện cạnh duct và khoảng cách giữa các tuyến mà không lặp một dim rời trên mỗi segment.
- Tool thử các lane hai phía từ 5 đến 200 mm trên giấy; ưu tiên lane gần và chỉ dùng rail xa khi khu vực gần đã bị tag/model chiếm chỗ.
- Nếu family không khai báo reference `Left/Right`, `Front/Back` hoặc `Center`, hướng dim tương ứng được bỏ qua thay vì tạo dim sai.
- Dimension do Smart Dim tạo được đánh dấu riêng. Chạy lại cùng selection sẽ thay kết quả cũ; dimension thủ công không bị xóa.

Smart Dim hỗ trợ Floor Plan, Ceiling Plan và Engineering Plan.
