using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.WinForms.Presentation;

namespace BilibiliDownloader.WinForms.Controls;

public sealed class QualitySelectorControl : UserControl
{
    private readonly ComboBox _qualityCombo;

    public QualitySelectorControl()
    {
        Height = 58;
        Dock = DockStyle.Top;
        BackColor = UiTheme.Surface;
        var label = UiTheme.CreateLabel("Chất lượng", 9F, FontStyle.Bold);
        label.Dock = DockStyle.Top;
        _qualityCombo = new ComboBox
        {
            Dock = DockStyle.Bottom,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Height = 32,
            Font = new Font("Segoe UI", 9.5F)
        };
        Controls.Add(_qualityCombo);
        Controls.Add(label);
    }

    public BilibiliStreamDto? SelectedStream => _qualityCombo.SelectedItem as BilibiliStreamDto;

    public void BindStreams(IReadOnlyList<BilibiliStreamDto> streams, BilibiliStreamDto? selected = null)
    {
        _qualityCombo.BeginUpdate();
        try
        {
            _qualityCombo.DataSource = null;
            _qualityCombo.DataSource = streams.ToArray();
            if (selected is not null)
            {
                _qualityCombo.SelectedItem = streams.FirstOrDefault(item => item.Id == selected.Id);
            }
        }
        finally
        {
            _qualityCombo.EndUpdate();
        }
    }
}
