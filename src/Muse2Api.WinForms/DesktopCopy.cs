namespace Muse2Api.WinForms;

internal static class DesktopCopy
{
    public static string FriendlyError(string message)
    {
        var text = message.ToLowerInvariant();
        if (text.Contains("upstream_response_unavailable"))
            return "App chưa đọc được phản hồi hoàn tất từ Muse web. Đã dừng chờ và giữ cuộc chat hiện tại, không tự gửi lại. Đây không phải thông báo hết lượt của tài khoản; xem Terminal trước khi tiếp tục.";
        if (text.Contains("stopped making progress") || text.Contains("web state unavailable") || text.Contains("web stopped responding") || text.Contains("web page closed"))
            return "Muse web đã ngừng phản hồi hoặc mất kết nối. App đã dừng chờ và giữ cuộc chat hiện tại; không tự gửi lại yêu cầu. Xem Terminal trước khi tiếp tục.";
        if (text.Contains("rate limit") || text.Contains("too many requests")) return "Muse đang giới hạn tần suất. Đã dừng gửi và giữ cuộc chat hiện tại. Hãy đợi rồi thử tiếp; không cần đăng nhập lại.";
        if (text.Contains("401") || text.Contains("api key")) return "Kết nối nội bộ cần được làm mới. Bấm Kết nối Muse để ứng dụng tự khôi phục.";
        if (text.Contains("upstream_auth") || text.Contains("login") || text.Contains("session rejected")) return "Phiên đăng nhập Muse đã hết hạn. Bấm Tài khoản Muse để đăng nhập lại.";
        if (text.Contains("429") || text.Contains("quota") || text.Contains("credits")) return "Tài khoản Muse đã hết lượt sử dụng. Bạn có thể thử lại sau hoặc đổi tài khoản.";
        if (text.Contains("available account") || text.Contains("no usable account") || text.Contains("cooldown")) return "Tài khoản Muse đang tạm chờ lượt xử lý. Phiên đăng nhập vẫn được lưu; hãy thử lại sau, không cần lấy mã mới.";
        if (text.Contains("timeout") || text.Contains("timed out")) return "Đã dừng chờ vì Muse quá lâu chưa trả kết quả. Cuộc chat được giữ lại và app không tự gửi lại; kiểm tra Terminal trước khi tiếp tục.";
        if (text.Contains("connection") || text.Contains("connect") || text.Contains("503")) return "Kết nối Muse đang gián đoạn. Bấm Kết nối Muse để thử lại.";
        if (text.Contains("binary") || text.Contains("2 mib")) return "Tệp này quá lớn hoặc không phải tệp văn bản. Hãy chọn một tệp mã nguồn nhỏ hơn.";
        if (text.Contains("outside") || text.Contains("junction") || text.Contains("symbolic")) return "Tệp được chọn nằm ngoài dự án hoặc là lối tắt. Hãy chọn tệp trực tiếp trong thư mục dự án.";
        return "Thao tác chưa thực hiện được. Bạn có thể thử lại; thông tin chi tiết nằm trong bảng Hoạt động.";
    }

    public static string Activity(string? tool, string? path, string detail)
    {
        var file = string.IsNullOrWhiteSpace(path) ? "tệp dự án" : Path.GetFileName(path);
        return tool switch
        {
            "read_file" => "Đang đọc " + file,
            "read_files" => "Đang đọc các tệp dự án",
            "write_file" => "Đang ghi " + file,
            "edit_file" => "Đang sửa " + file,
            "search_files" => "Đang tìm kiếm trong dự án",
            "list_files" => "Đang tìm hiểu cấu trúc dự án",
            "git_status" or "git_diff" => "Đang xem các thay đổi của dự án",
            "run_command" when detail.Contains("test", StringComparison.OrdinalIgnoreCase) => "Đang chạy kiểm tra dự án",
            "run_command" when detail.Contains("build", StringComparison.OrdinalIgnoreCase) => "Đang kiểm tra bản dựng của dự án",
            "run_command" => "Đang thực hiện thao tác trong dự án",
            "process_status" => "Đang chờ lệnh đang chạy trả kết quả",
            "process_stop" => "Đang dừng lệnh",
            "process_input" => "Đang gửi dữ liệu tới lệnh đang chạy",
            "computer_inspect" => "Đang đọc giao diện cửa sổ đã chọn",
            "computer_screenshot" => "Đang chụp cửa sổ đã chọn",
            "computer_assert" => "Đang xác nhận kết quả trên giao diện",
            "computer_invoke" or "computer_click" => "Đang gửi thao tác bấm trên cửa sổ đã chọn",
            "computer_type" or "computer_set_value" => "Đang gửi dữ liệu tới ô nhập đã chọn",
            _ when tool?.StartsWith("computer_") == true => "Đang thao tác trên cửa sổ đã chọn · Esc để dừng",
            _ => "Đang xử lý yêu cầu",
        };
    }

    public static string CompletedActivity(string activity) => activity.StartsWith("Đang ") ? "Đã " + activity[5..] + "." : "Đã hoàn tất thao tác.";
}
