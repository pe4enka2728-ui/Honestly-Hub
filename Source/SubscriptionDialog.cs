namespace VpnZapretSwitch;

internal sealed class SubscriptionDialog : Form
{
    private readonly TextBox input = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        BackColor = Astra.Input, ForeColor = Astra.Text, UseSystemPasswordChar = true,
        PlaceholderText = "https://get.s-protect.com/…", Font = new Font("Segoe UI", 10) };
    private string? fileContent;
    public string Content => fileContent ?? input.Text.Trim();
    public bool FromFile => fileContent is not null;
    public SubscriptionDialog()
    {
        Text = "Добавить подписку · Honestly TUN"; Icon = HonestlyBrand.LoadIcon();
        BackColor = Astra.Background; ForeColor = Astra.Text; Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 278); FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(24);
        var layout = new AstraLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 34, 52, 48, 40, 54 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(Astra.Label("Персональная ссылка подписки", 16, style: FontStyle.Bold), 0, 0);
        var note = Astra.Label("Скопируйте полную ссылку из кабинета провайдера.\nМожно также вставить один ключ VLESS/VMess/Trojan/SS или импортировать файл.", 9, Astra.Muted);
        note.AutoSize = false; note.Dock = DockStyle.Fill;
        layout.Controls.Add(note, 0, 1);
        var panel = new AstraPanel { Dock = DockStyle.Fill, Fill = Astra.Input, Radius = 9, Padding = new Padding(12, 12, 12, 8), Margin = new Padding(0, 0, 0, 6) };
        panel.Controls.Add(input); layout.Controls.Add(panel, 0, 2);
        var fileNote = Astra.Label("Ключи сохраняются зашифрованными. Провайдер может учесть клиент\nкак новое устройство при загрузке подписки.", 9, Astra.Muted);
        fileNote.AutoSize = false; fileNote.Dock = DockStyle.Fill; fileNote.Padding = new Padding(0, 6, 0, 0);
        layout.Controls.Add(fileNote, 0, 3);
        var actions = new AstraFlowPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var submit = new AstraButton { Text = "Добавить", Kind = ButtonKind.Primary, Size = new Size(146, 42), Margin = new Padding(0, 0, 10, 0) };
        var file = new AstraButton { Text = "Из файла…", Size = new Size(146, 42), Margin = new Padding(0, 0, 10, 0) };
        var cancel = new AstraButton { Text = "Отмена", Size = new Size(120, 42), DialogResult = DialogResult.Cancel };
        submit.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(Content)) { input.Focus(); return; }
            DialogResult = DialogResult.OK; Close();
        };
        input.TextChanged += (_, _) => { if (input.Focused) { fileContent = null; input.UseSystemPasswordChar = true; } };
        file.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Подписки и ключи|*.json;*.txt|Все файлы|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > 8_000_000) throw new IOException("Файл превышает 8 МБ.");
                fileContent = File.ReadAllText(dialog.FileName);
                input.UseSystemPasswordChar = false; input.Text = "Файл готов к импорту";
                input.ReadOnly = true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        actions.Controls.Add(submit); actions.Controls.Add(file); actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 4); Controls.Add(layout); AcceptButton = submit; CancelButton = cancel;
    }
}
