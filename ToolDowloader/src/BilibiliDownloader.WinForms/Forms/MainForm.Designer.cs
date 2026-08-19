using BilibiliDownloader.WinForms.Controls;
using BilibiliDownloader.WinForms.Presentation;

namespace BilibiliDownloader.WinForms.Forms;

partial class MainForm
{
    private TextBox _urlTextBox = null!;
    private Button _analyzeButton = null!;
    private Button _downloadButton = null!;
    private Button _browseOutputButton = null!;
    private Button _settingsButton = null!;
    private Button _historyButton = null!;
    private Button _aboutButton = null!;
    private TextBox _outputFolderTextBox = null!;
    private ComboBox _formatComboBox = null!;
    private VideoInfoControl _videoInfo = null!;
    private QualitySelectorControl _qualitySelector = null!;
    private Panel _videoCard = null!;
    private FlowLayoutPanel _downloadQueue = null!;
    private Label _statusLabel = null!;

    private void InitializeComponent()
    {
        SuspendLayout();
        Text = "Bilibili Downloader";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 680);
        ClientSize = new Size(1120, 820);
        BackColor = UiTheme.Background;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24, 0, 24, 18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 248));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateAnalyzeCard(), 0, 1);
        root.Controls.Add(CreateVideoCard(), 0, 2);
        root.Controls.Add(CreateQueueHeader(), 0, 3);

        _downloadQueue = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = UiTheme.Background,
            Padding = new Padding(0, 0, 6, 0)
        };
        _downloadQueue.Resize += (_, _) => ResizeQueueItems();
        root.Controls.Add(_downloadQueue, 0, 4);
        Controls.Add(root);
        ResumeLayout(performLayout: true);
    }

    private Control CreateHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background };
        var title = UiTheme.CreateLabel("Bilibili Downloader", 18F, FontStyle.Bold);
        title.ForeColor = UiTheme.PrimaryDark;
        title.Location = new Point(0, 21);

        _settingsButton = UiTheme.CreateSecondaryButton("⚙  Cài đặt");
        _historyButton = UiTheme.CreateSecondaryButton("Lịch sử");
        _aboutButton = UiTheme.CreateSecondaryButton("Giới thiệu");
        foreach (var button in new[] { _settingsButton, _historyButton, _aboutButton })
        {
            button.Width = 108;
        }

        var navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 354,
            Height = 72,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 18, 0, 0)
        };
        navigation.Controls.Add(_historyButton);
        navigation.Controls.Add(_settingsButton);
        navigation.Controls.Add(_aboutButton);
        panel.Controls.Add(navigation);
        panel.Controls.Add(title);
        return panel;
    }

    private Control CreateAnalyzeCard()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(18, 14, 18, 14)
        };
        panel.Paint += UiTheme.PaintRoundedPanel;
        var label = UiTheme.CreateLabel("URL VIDEO BILIBILI", 8.5F, FontStyle.Bold);
        label.ForeColor = UiTheme.MutedText;
        label.Dock = DockStyle.Top;

        _urlTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10F),
            PlaceholderText = "https://www.bilibili.com/video/BVxxxxxxxxxx/",
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 8, 8, 0)
        };
        _analyzeButton = UiTheme.CreatePrimaryButton("PHÂN TÍCH VIDEO");
        _analyzeButton.Dock = DockStyle.Fill;
        _analyzeButton.Margin = new Padding(12, 0, 0, 0);

        var input = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 42, ColumnCount = 2 };
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        input.Controls.Add(_urlTextBox, 0, 0);
        input.Controls.Add(_analyzeButton, 1, 0);
        panel.Controls.Add(input);
        panel.Controls.Add(label);
        return panel;
    }

    private Control CreateVideoCard()
    {
        _videoCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Margin = new Padding(0, 0, 0, 12),
            Visible = false
        };
        _videoCard.Paint += UiTheme.PaintRoundedPanel;
        _videoInfo = new VideoInfoControl();

        var options = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 18, 14),
            BackColor = UiTheme.Surface
        };
        _qualitySelector = new QualitySelectorControl();
        _formatComboBox = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9.5F),
            Height = 32
        };
        _formatComboBox.Items.Add("MP4");
        _formatComboBox.SelectedIndex = 0;
        var formatLabel = UiTheme.CreateLabel("Định dạng", 9F, FontStyle.Bold);
        formatLabel.Dock = DockStyle.Top;

        _outputFolderTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Segoe UI", 9F),
            BorderStyle = BorderStyle.FixedSingle
        };
        _browseOutputButton = UiTheme.CreateSecondaryButton("Chọn...");
        _browseOutputButton.Dock = DockStyle.Fill;
        _browseOutputButton.Width = 76;
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, ColumnCount = 2 };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        folderRow.Controls.Add(_outputFolderTextBox, 0, 0);
        folderRow.Controls.Add(_browseOutputButton, 1, 0);
        var folderLabel = UiTheme.CreateLabel("Thư mục lưu", 9F, FontStyle.Bold);
        folderLabel.Dock = DockStyle.Top;

        _downloadButton = UiTheme.CreatePrimaryButton("DOWNLOAD");
        _downloadButton.Dock = DockStyle.Bottom;
        _downloadButton.Height = 40;

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1 };
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fields.Controls.Add(_qualitySelector, 0, 0);
        fields.Controls.Add(formatLabel, 0, 1);
        fields.Controls.Add(_formatComboBox, 0, 2);
        fields.Controls.Add(folderLabel, 0, 3);
        fields.Controls.Add(folderRow, 0, 4);
        options.Controls.Add(_downloadButton);
        options.Controls.Add(fields);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        layout.Controls.Add(_videoInfo, 0, 0);
        layout.Controls.Add(options, 1, 0);
        _videoCard.Controls.Add(layout);
        return _videoCard;
    }

    private Control CreateQueueHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var title = UiTheme.CreateLabel("DOWNLOAD QUEUE", 10F, FontStyle.Bold);
        title.Location = new Point(0, 17);
        _statusLabel = UiTheme.CreateLabel("Sẵn sàng", 9F);
        _statusLabel.ForeColor = UiTheme.MutedText;
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _statusLabel.Location = new Point(900, 18);
        panel.Controls.Add(title);
        panel.Controls.Add(_statusLabel);
        panel.Resize += (_, _) => _statusLabel.Left = panel.ClientSize.Width - _statusLabel.Width;
        return panel;
    }

    private void ResizeQueueItems()
    {
        var width = Math.Max(300, _downloadQueue.ClientSize.Width - _downloadQueue.Padding.Horizontal - 22);
        foreach (Control control in _downloadQueue.Controls)
        {
            control.Width = width;
        }
    }
}
