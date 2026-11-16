using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AugurWorkspace
{
	public partial class MainForm : Form
	{
		private static readonly System.Net.Http.HttpClient _httpClient = new();
		// Подключение WinAPI для темного системного заголовка окна
		[DllImport("dwmapi.dll")]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
		private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

		private readonly string _profilesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),	"AugurWorkspace",	"Profiles");

		private ProfileConfig? _currentProfile;
		private CoreWebView2Environment? _webEnv;

		private readonly Dictionary<string, WebView2> _loadedTabs = new();
		private readonly Dictionary<string, Button> _sidebarButtons = new();
		private readonly Dictionary<string, Panel> _sidebarRows = new();
		private readonly Dictionary<string, PictureBox> _sidebarIcons = new();
		private string? _activeTabId;
		private bool _isSidebarCollapsed = false;
		private bool _isUpdatingProfileList = false;

		private Panel _launcherPanel = null!;
		private Panel _workspacePanel = null!;
		private Panel _sidebarPanel = null!;
		private SmoothScrollFlowPanel _bookmarksFlow = null!;
		private Panel _customScrollTrack = null!;
		private Panel _customScrollThumb = null!;
		private Panel _contentPanel = null!;
		private ComboBox _profileSwitcher = null!;

		private Label _topTitleLabel = null!;
		private TextBox _addressBar = null!;
		private Button _btnSaveBaseUrl = null!;
		private Button _btnSaveAsNewBookmark = null!;
		private readonly ToolTip _toolTip = new();

		private const string ADD_PROFILE_ITEM = "+ Новый профиль...";

		// Палитра цветов
		private static readonly Color BgWindow = Color.FromArgb(24, 24, 27);
		private static readonly Color BgSidebar = Color.FromArgb(30, 31, 34);
		private static readonly Color BgTopBar = Color.FromArgb(26, 27, 30);
		private static readonly Color BgRowNormal = Color.FromArgb(38, 40, 45);
		private static readonly Color BgRowActive = Color.FromArgb(58, 60, 68);
		private static readonly Color BgHoverBright = Color.FromArgb(228, 230, 235);
		private static readonly Color TextDarkHover = Color.FromArgb(18, 19, 22);

		private static void ApplyDarkTitleBar(IntPtr handle)
		{
			try
			{
				int dark = 1;
				DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
			}
			catch { }
		}

		private static ContextMenuStrip CreateDarkContextMenu()
		{
			var ctx = new ContextMenuStrip
			{
				Renderer = new DarkMenuRenderer(),
				BackColor = BgSidebar,
				ForeColor = Color.Gainsboro,
				ShowImageMargin = false
			};
			return ctx;
		}


		public MainForm()
		{
			InitializeComponent();
			this.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
			ApplyDarkTitleBar(this.Handle);

			// Добавляем браузерный User-Agent для стабильной загрузки исошек
			if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
			{
				_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
			}

			this.Text = "Augur Workspace";
			this.Size = new Size(1360, 820);
			this.StartPosition = FormStartPosition.CenterScreen;
			this.BackColor = BgWindow;
			this.ForeColor = Color.White;

			Directory.CreateDirectory(_profilesRoot);
			EnsureDefaultProfiles();
			BuildUI();
			ShowLauncher();
		}

		private static (string normalizedTarget, bool isLocalApp) NormalizeTarget(string rawInput)
		{
			string clean = rawInput.Trim().Trim('"');
			if (string.IsNullOrWhiteSpace(clean))
				return (clean, false);

			// 1. Если протокол уже указан — это точно веб-ссылка
			if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
				clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return (clean, false);
			}

			// 2. Если есть слэши Windows (\), буква диска (C:) или известное расширение файла — это локальный файл
			bool looksLikeFile = clean.Contains('\\') ||
								 System.Text.RegularExpressions.Regex.IsMatch(clean, @"^[a-zA-Z]:") ||
								 System.Text.RegularExpressions.Regex.IsMatch(clean, @"\.(exe|bat|cmd|msi|xlsx?|docx?|pdf|txt|ahk|ps1|vbs|lnk)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

			if (looksLikeFile)
			{
				return (clean, true);
			}

			// 3. Если похоже на домен (://google.com, poe.ninja), localhost или IP-адрес — подставляем протокол
			bool looksLikeDomain = System.Text.RegularExpressions.Regex.IsMatch(
				clean,
				@"^(localhost(:\d+)?|(\d{1,3}\.){3}\d{1,3}(:\d+)?|([a-zA-Z0-9-]+\.)+[a-zA-Z]{2,})(:\d+)?(/.*)?$"
			);

			if (looksLikeDomain)
			{
				string scheme = clean.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
								System.Text.RegularExpressions.Regex.IsMatch(clean, @"^(\d{1,3}\.){3}\d{1,3}")
								? "http://" : "https://";
				return (scheme + clean, false);
			}

			// Всё остальное (например, короткие команды) считаем локальным запуском
			return (clean, true);
		}


		private void EnableDarkTitleBar()
		{
			try
			{
				int useImmersiveDarkMode = 1;
				DwmSetWindowAttribute(this.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));
			}
			catch { }
		}

		private void EnsureDefaultProfiles()
		{
			if (Directory.GetDirectories(_profilesRoot).Length == 0)
			{
				SaveProfileConfig(new ProfileConfig
				{
					Name = "Work",
					Bookmarks = new List<BookmarkItem>
			{
				new() { Title = "Localhost 8080", Target = "http://localhost:8080" },
				new() { Title = "Блокнот", Target = "notepad.exe", IsLocalApp = true }
			}
				});

				SaveProfileConfig(new ProfileConfig
				{
					Name = "Personal",
					Bookmarks = new List<BookmarkItem>
			{
				new() { Title = "Search", Target = "https://google.com" },
				new() { Title = "VideoWithCats", Target = "https://www.youtube.com/results?search_query=cats" },
			}
				});
			}
		}

		private void BuildUI()
		{
			_launcherPanel = new Panel { Dock = DockStyle.Fill, BackColor = BgWindow };
			this.Controls.Add(_launcherPanel);

			_workspacePanel = new Panel { Dock = DockStyle.Fill, Visible = false };
			this.Controls.Add(_workspacePanel);

			_sidebarPanel = new Panel
			{
				Dock = DockStyle.Left,
				Width = 255,
				BackColor = BgSidebar,
				Padding = new Padding(6)
			};

			var sideTop = new Panel { Dock = DockStyle.Top, Height = 40 };
			var btnToggle = new Button
			{
				Text = "≡",
				Width = 36,
				Dock = DockStyle.Left,
				FlatStyle = FlatStyle.Flat,
				ForeColor = Color.White,
				Font = new Font("Segoe UI", 12, FontStyle.Bold),
				Cursor = Cursors.Hand
			};
			btnToggle.FlatAppearance.BorderSize = 0;
			ApplyContrastHover(btnToggle, Color.Transparent, Color.White);
			btnToggle.Click += (s, e) => ToggleSidebar();

			_profileSwitcher = new ComboBox
			{
				Dock = DockStyle.Fill,
				DropDownStyle = ComboBoxStyle.DropDownList,
				BackColor = BgRowNormal,
				ForeColor = Color.White,
				FlatStyle = FlatStyle.Flat,
				Font = new Font("Segoe UI", 10)
			};
			_profileSwitcher.SelectedIndexChanged += async (s, e) =>
			{
				if (_isUpdatingProfileList) return;
				if (_profileSwitcher.SelectedItem is string selected)
				{
					if (selected == ADD_PROFILE_ITEM)
						CreateProfileFromSwitcher();
					else if (_currentProfile?.Name != selected)
						await SwitchProfileAsync(selected);
				}
			};

			sideTop.Controls.Add(_profileSwitcher);
			sideTop.Controls.Add(btnToggle);

			var btnAddBookmark = new Button
			{
				Text = "+ Добавить закладку",
				Dock = DockStyle.Bottom,
				Height = 36,
				FlatStyle = FlatStyle.Flat,
				BackColor = BgRowNormal,
				ForeColor = Color.White,
				Cursor = Cursors.Hand
			};
			btnAddBookmark.FlatAppearance.BorderSize = 0;
			ApplyContrastHover(btnAddBookmark, BgRowNormal, Color.White);
			btnAddBookmark.Click += (s, e) => AddNewBookmarkDialog();

			// Контейнер для списка закладок и кастомного тёмного скроллбара
			var scrollWrapper = new Panel
			{
				Dock = DockStyle.Fill,
				Padding = new Padding(0, 8, 0, 8),
				BackColor = Color.Transparent
			};

			_customScrollTrack = new Panel
			{
				Dock = DockStyle.Right,
				Width = 6,
				BackColor = Color.FromArgb(24, 25, 28),
				Visible = false
			};

			_customScrollThumb = new Panel
			{
				Width = 6,
				Height = 40,
				BackColor = Color.FromArgb(75, 78, 88),
				Location = new Point(0, 0),
				Cursor = Cursors.Hand
			};
			_customScrollTrack.Controls.Add(_customScrollThumb);

			bool isDraggingThumb = false;
			int dragStartY = 0;
			int thumbStartTop = 0;

			_customScrollThumb.MouseDown += (s, e) =>
			{
				if (e.Button == MouseButtons.Left)
				{
					isDraggingThumb = true;
					dragStartY = Cursor.Position.Y;
					thumbStartTop = _customScrollThumb.Top;
				}
			};

			_customScrollThumb.MouseMove += (s, e) =>
			{
				if (!isDraggingThumb) return;
				int maxThumbTop = _customScrollTrack.Height - _customScrollThumb.Height;
				if (maxThumbTop <= 0) return;

				int newTop = Math.Clamp(thumbStartTop + (Cursor.Position.Y - dragStartY), 0, maxThumbTop);
				_customScrollThumb.Top = newTop;

				int contentHeight = 0;
				foreach (Control c in _bookmarksFlow.Controls)
					contentHeight += c.Height + c.Margin.Vertical;

				int maxScroll = contentHeight - _bookmarksFlow.ClientSize.Height;
				if (maxScroll > 0)
				{
					float ratio = (float)newTop / maxThumbTop;
					_bookmarksFlow.AutoScrollPosition = new Point(0, (int)(ratio * maxScroll));
				}
			};

			_customScrollThumb.MouseUp += (s, e) => isDraggingThumb = false;

			var clipPanel = new Panel
			{
				Dock = DockStyle.Fill,
				BackColor = Color.Transparent
			};

			_bookmarksFlow = new SmoothScrollFlowPanel
			{
				Location = new Point(0, 0),
				FlowDirection = FlowDirection.TopDown,
				WrapContents = false,
				AutoScroll = true,
				HorizontalScroll = { Enabled = false, Visible = false },
				AllowDrop = true,
				Padding = new Padding(0)
			};
			_bookmarksFlow.Scrolled += UpdateCustomScrollbar;


			clipPanel.Resize += (s, e) =>
			{
				int sysScrollW = SystemInformation.VerticalScrollBarWidth;
				_bookmarksFlow.Size = new Size(clipPanel.Width + sysScrollW, clipPanel.Height);
				UpdateCustomScrollbar();
			};

			_bookmarksFlow.MouseWheel += (s, e) => UpdateCustomScrollbar();
			_bookmarksFlow.Scroll += (s, e) => UpdateCustomScrollbar();
			_bookmarksFlow.ControlAdded += (s, e) => UpdateCustomScrollbar();
			_bookmarksFlow.ControlRemoved += (s, e) => UpdateCustomScrollbar();

			_bookmarksFlow.DragEnter += (s, e) =>
			{
				if (e.Data != null && e.Data.GetDataPresent(typeof(BookmarkItem)))
					e.Effect = DragDropEffects.Move;
			};
			_bookmarksFlow.DragDrop += BookmarksFlow_DragDrop;

			clipPanel.Controls.Add(_bookmarksFlow);
			scrollWrapper.Controls.Add(_customScrollTrack); // Сначала прижимаем скроллбар к правому краю
			scrollWrapper.Controls.Add(clipPanel);          // Затем clipPanel занимает оставшееся место слева от него

			_sidebarPanel.Controls.Add(scrollWrapper);
			_sidebarPanel.Controls.Add(btnAddBookmark);
			_sidebarPanel.Controls.Add(sideTop);

			// Правая область (верхняя адресная панель + контейнер вкладок)
			var rightArea = new Panel { Dock = DockStyle.Fill };

			var topBarPanel = new Panel
			{
				Dock = DockStyle.Top,
				Height = 38,
				BackColor = BgTopBar,
				Padding = new Padding(10, 5, 8, 5)
			};

			_topTitleLabel = new Label
			{
				Dock = DockStyle.Left,
				AutoSize = true,
				TextAlign = ContentAlignment.MiddleLeft,
				Padding = new Padding(0, 4, 10, 0),
				ForeColor = Color.White,
				Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
				Text = "Выберите закладку"
			};

			_btnSaveBaseUrl = new Button
			{
				Text = "💾",
				Width = 36,
				Dock = DockStyle.Right,
				FlatStyle = FlatStyle.Flat,
				BackColor = BgRowNormal,
				ForeColor = Color.Gainsboro,
				Font = new Font("Segoe UI Emoji", 10f),
				Cursor = Cursors.Hand,
				Enabled = false
			};
			_btnSaveBaseUrl.FlatAppearance.BorderSize = 0;
			ApplyContrastHover(_btnSaveBaseUrl, BgRowNormal, Color.Gainsboro);
			_btnSaveBaseUrl.Click += async (s, e) => await SaveCurrentUrlAsBaseAsync();
			_toolTip.SetToolTip(_btnSaveBaseUrl, "Сохранить текущий адрес как базовый для этой вкладки (для кнопки «Домой»)");

			_btnSaveAsNewBookmark = new Button
			{
				Text = "☆",
				Width = 36,
				Dock = DockStyle.Right,
				FlatStyle = FlatStyle.Flat,
				BackColor = BgRowNormal,
				ForeColor = Color.Gainsboro,
				Font = new Font("Segoe UI Emoji", 11f),
				Cursor = Cursors.Hand,
				Enabled = false
			};
			_btnSaveAsNewBookmark.FlatAppearance.BorderSize = 0;
			ApplyContrastHover(_btnSaveAsNewBookmark, BgRowNormal, Color.Gainsboro);
			_btnSaveAsNewBookmark.Click += async (s, e) => await SaveCurrentUrlAsNewBookmarkAsync();
			_toolTip.SetToolTip(_btnSaveAsNewBookmark, "Сохранить текущий адрес как новую закладку (встанет сразу под текущей)");

			_addressBar = new TextBox
			{
				Dock = DockStyle.Fill,
				BackColor = Color.FromArgb(35, 37, 42),
				ForeColor = Color.LightGray,
				BorderStyle = BorderStyle.FixedSingle,
				Font = new Font("Segoe UI", 9.5f),
				Enabled = false
			};
			_addressBar.KeyDown += (s, e) =>
			{
				if (e.KeyCode == Keys.Enter)
				{
					e.SuppressKeyPress = true;
					NavigateAddressBar();
				}
			};

			var addrCtx = CreateDarkContextMenu();
			addrCtx.Items.Add("Копировать", null, (s, e) => { if (!string.IsNullOrEmpty(_addressBar.Text)) Clipboard.SetText(_addressBar.Text); });
			addrCtx.Items.Add("Вставить", null, (s, e) => { if (Clipboard.ContainsText()) _addressBar.SelectedText = Clipboard.GetText(); });
			addrCtx.Items.Add("Выделить всё", null, (s, e) => _addressBar.SelectAll());
			_addressBar.ContextMenuStrip = addrCtx;


			var spacer1 = new Panel { Width = 8, Dock = DockStyle.Right, BackColor = Color.Transparent };
			var spacer2 = new Panel { Width = 4, Dock = DockStyle.Right, BackColor = Color.Transparent };

			topBarPanel.Controls.Add(_addressBar);
			topBarPanel.Controls.Add(spacer1);               // Пробел 8px после строки
			topBarPanel.Controls.Add(_btnSaveAsNewBookmark); // Звезда
			topBarPanel.Controls.Add(spacer2);               // Пробел 4px между звездой и дискеткой
			topBarPanel.Controls.Add(_btnSaveBaseUrl);		 // Дискета
			topBarPanel.Controls.Add(_topTitleLabel);

			_contentPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 18, 20) };
			rightArea.Controls.Add(_contentPanel);
			rightArea.Controls.Add(topBarPanel);

			_workspacePanel.Controls.Add(rightArea);
			_workspacePanel.Controls.Add(_sidebarPanel);
		}

		// Универсальный хелпер контрастного ховера: светлый фон -> темный текст
		private static void ApplyContrastHover(Button btn, Color normalBg, Color normalFg)
		{
			btn.FlatAppearance.MouseOverBackColor = BgHoverBright;
			btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(200, 204, 212);
			btn.MouseEnter += (s, e) => btn.ForeColor = TextDarkHover;
			btn.MouseLeave += (s, e) => btn.ForeColor = normalFg;
		}
		private void ShowLauncher()
		{
			_launcherPanel.Controls.Clear();
			_launcherPanel.Visible = true;
			_workspacePanel.Visible = false;
			var title = new Label
			{
				Text = "AUGUR WORKSPACE",
				Font = new Font("Segoe UI", 22, FontStyle.Bold),
				ForeColor = Color.White,
				AutoSize = true,
				Location = new Point(60, 50)
			};
			_launcherPanel.Controls.Add(title);
			var flow = new FlowLayoutPanel
			{
				Location = new Point(60, 120),
				Size = new Size(1100, 500),
				AutoScroll = true
			};
			foreach (var dir in Directory.GetDirectories(_profilesRoot))
			{
				string profName = Path.GetFileName(dir);
				var card = new Button
				{
					Text = profName,
					Size = new Size(200, 120),
					Margin = new Padding(10),
					FlatStyle = FlatStyle.Flat,
					BackColor = BgRowNormal,
					ForeColor = Color.White,
					Font = new Font("Segoe UI", 13, FontStyle.Bold),
					Cursor = Cursors.Hand
				};
				card.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 80);
				ApplyContrastHover(card, BgRowNormal, Color.White);
				card.Click += async (s, e) => await SwitchProfileAsync(profName);

				// Контекстное меню по правой кнопке мыши на карточку профиля
				var ctx = CreateDarkContextMenu();
				ctx.Items.Add("Переименовать профиль", null, (s, e) => RenameProfile(profName));
				ctx.Items.Add(new ToolStripSeparator());
				ctx.Items.Add("Удалить профиль", null, (s, e) => DeleteProfile(profName));
				card.ContextMenuStrip = ctx;

				flow.Controls.Add(card);
			}

			var btnNewProf = new Button
			{
				Text = "+ Новый профиль",
				Size = new Size(200, 120),
				Margin = new Padding(10),
				FlatStyle = FlatStyle.Flat,
				BackColor = Color.FromArgb(28, 29, 33),
				ForeColor = Color.Gray,
				Font = new Font("Segoe UI", 11),
				Cursor = Cursors.Hand
			};
			ApplyContrastHover(btnNewProf, Color.FromArgb(28, 29, 33), Color.Gray);
			btnNewProf.Click += (s, e) => CreateProfileFromLauncher();
			flow.Controls.Add(btnNewProf);
			_launcherPanel.Controls.Add(flow);
		}
		private async Task SwitchProfileAsync(string profileName)
		{
			foreach (var wv in _loadedTabs.Values) wv.Dispose();
			_loadedTabs.Clear();
			_contentPanel.Controls.Clear();
			_activeTabId = null;
			string profDir = Path.Combine(_profilesRoot, profileName);
			string cfgPath = Path.Combine(profDir, "bookmarks.json");
			_currentProfile = (ProfileConfig)JsonSerializer.Deserialize(File.ReadAllText(cfgPath), typeof(ProfileConfig))!;
			string userDataDir = Path.Combine(profDir, "UserData");
			var options = new CoreWebView2EnvironmentOptions(
			"--disable-features=UseEcoQoSForBackgroundProcess " +
			"--disable-background-timer-throttling " +
			"--disable-renderer-backgrounding " +
			"--disable-backgrounding-occluded-windows"
			);
			_webEnv = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
			RefreshProfileSwitcher(profileName);
			this.Text = $"Augur Workspace — {profileName}";
			ResetTopBar();
			RenderSidebarBookmarks();
			_launcherPanel.Visible = false;
			_workspacePanel.Visible = true;
			_workspacePanel.PerformLayout();
			_bookmarksFlow.Size = new Size(_bookmarksFlow.Parent!.Width + SystemInformation.VerticalScrollBarWidth, _bookmarksFlow.Parent.Height);
			UpdateCustomScrollbar();

		}
		private void ResetTopBar()
		{
			_topTitleLabel.Text = "Выберите закладку";
			_addressBar.Text = "";
			_addressBar.Enabled = false;
			_btnSaveBaseUrl.Enabled = false;
			_btnSaveAsNewBookmark.Enabled = false;
		}
		private void RefreshProfileSwitcher(string activeName)
		{
			_isUpdatingProfileList = true;
			_profileSwitcher.Items.Clear();
			foreach (var dir in Directory.GetDirectories(_profilesRoot))
				_profileSwitcher.Items.Add(Path.GetFileName(dir));
			_profileSwitcher.Items.Add(ADD_PROFILE_ITEM);
			_profileSwitcher.SelectedItem = activeName;
			_isUpdatingProfileList = false;
		}
		private string GetFaviconCachePath(BookmarkItem item)
		{
			string favDir = Path.Combine(_profilesRoot, _currentProfile?.Name ?? "Default", "Favicons");
			Directory.CreateDirectory(favDir);

			if (!item.IsLocalApp && Uri.TryCreate(item.Target, UriKind.Absolute, out var uri))
			{
				// Имя файла по домену сайта — одна иконка на все вкладки этого домена
				string safeHost = uri.Host.Replace(".", "_").Replace(":", "_");
				return Path.Combine(favDir, $"{safeHost}.png");
			}

			return Path.Combine(favDir, $"{item.Id}.png");
		}

		private Image LoadOrCreateBookmarkIcon(BookmarkItem item)
		{
			string cachedPath = GetFaviconCachePath(item);
			if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 50)
			{
				try
				{
					byte[] bytes = File.ReadAllBytes(cachedPath);
					using var ms = new MemoryStream(bytes);
					using var tempBmp = new Bitmap(ms);
					return new Bitmap(tempBmp); // Копируем в независимый Bitmap
				}
				catch { }
			}

			// 1. Для локальных программ (исправлено сохранение GDI+)
			if (item.IsLocalApp)
			{
				string resolvedPath = item.Target.Trim().Trim('"');
				if (!File.Exists(resolvedPath))
				{
					string sys32 = Path.Combine(Environment.SystemDirectory, resolvedPath);
					string winDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), resolvedPath);
					if (File.Exists(sys32)) resolvedPath = sys32;
					else if (File.Exists(winDir)) resolvedPath = winDir;
				}

				if (File.Exists(resolvedPath))
				{
					try
					{
						using var ico = Icon.ExtractAssociatedIcon(resolvedPath);
						if (ico != null)
						{
							using var rawBmp = ico.ToBitmap();
							var cleanBmp = new Bitmap(rawBmp); // Разрываем связь с ресурсом Win32

							using var ms = new MemoryStream();
							cleanBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
							File.WriteAllBytes(cachedPath, ms.ToArray());

							return cleanBmp;
						}
					}
					catch { }
				}
			}
			// 2. Для веб-сайтов: фоновая загрузка по 4 источникам
			else if (!item.IsLocalApp && Uri.TryCreate(item.Target, UriKind.Absolute, out var uri))
			{
				_ = Task.Run(async () =>
				{
					byte[]? data = null;
					string[] sources =
					{
				$"https://yandex.net{uri.Host}?size=32",
				$"https://google.com{uri.Host}&sz=64",
				$"https://duckduckgo.com{uri.Host}.ico",
				$"{uri.Scheme}://{uri.Host}/favicon.ico"
			};

					foreach (var src in sources)
					{
						try
						{
							var downloaded = await _httpClient.GetByteArrayAsync(src);
							if (downloaded.Length > 50)
							{
								using var testMs = new MemoryStream(downloaded);
								using var testBmp = new Bitmap(testMs);
								data = downloaded;
								break;
							}
						}
						catch { }
					}

					if (data != null)
					{
						try
						{
							await File.WriteAllBytesAsync(cachedPath, data);
							if (!this.IsDisposed && this.IsHandleCreated)
							{
								this.Invoke(() =>
								{
									if (_sidebarIcons.TryGetValue(item.Id, out var picBox) && !picBox.IsDisposed)
									{
										using var ms = new MemoryStream(data);
										using var tempBmp = new Bitmap(ms);
										picBox.Image = new Bitmap(tempBmp);
										picBox.Refresh();
									}
								});
							}
						}
						catch { }
					}
				});
			}

			// Временный серый кружок с первой буквой, пока скачивается иконка
			var bmp = new Bitmap(24, 24);
			using (var g = Graphics.FromImage(bmp))
			{
				g.SmoothingMode = SmoothingMode.AntiAlias;
				using var brush = new SolidBrush(Color.FromArgb(52, 54, 62));
				g.FillEllipse(brush, 0, 0, 23, 23);

				string letter = string.IsNullOrWhiteSpace(item.Title) ? "?" : item.Title.Substring(0, 1).ToUpper();
				using var font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
				var sz = g.MeasureString(letter, font);
				g.DrawString(letter, font, Brushes.Gainsboro, (24 - sz.Width) / 2, (24 - sz.Height) / 2);
			}
			return bmp;
		}



		private void RenderSidebarBookmarks()
		{
			_bookmarksFlow.Controls.Clear();
			_sidebarButtons.Clear();
			_sidebarRows.Clear();
			_sidebarIcons.Clear();
			if (_currentProfile == null) return;
			int rowWidth = _isSidebarCollapsed ? 40 : _sidebarPanel.Width - 24;
			for (int i = 0; i < _currentProfile.Bookmarks.Count; i++)
			{
				var item = _currentProfile.Bookmarks[i];
				int currentIndex = i;
				var rowPanel = new Panel
				{
					Width = rowWidth,
					Height = 40,
					Margin = new Padding(0, 2, 0, 2),
					BackColor = BgRowNormal
				};
				// Контейнер для круглого значка (как на скрине из Chrome)
				var iconWrap = new Panel
				{
					Width = 38,
					Dock = DockStyle.Left,
					BackColor = Color.Transparent,
					Cursor = Cursors.Hand
				};
				var picIcon = new PictureBox
				{
					Size = new Size(22, 22),
					Location = new Point(8, 9),
					SizeMode = PictureBoxSizeMode.Zoom,
					BackColor = Color.Transparent,
					Image = LoadOrCreateBookmarkIcon(item),
					Cursor = Cursors.Hand
				};
				picIcon.Click += async (s, e) => await OpenBookmarkAsync(item);
				iconWrap.Click += async (s, e) => await OpenBookmarkAsync(item);
				iconWrap.Controls.Add(picIcon);
				var btnMain = new Button
				{
					Text = _isSidebarCollapsed ? "" : item.Title,
					Tag = item,
					Dock = DockStyle.Fill,
					TextAlign = ContentAlignment.MiddleLeft,
					Padding = new Padding(2, 0, 0, 0),
					FlatStyle = FlatStyle.Flat,
					BackColor = Color.Transparent,
					ForeColor = Color.Gainsboro,
					Font = new Font("Segoe UI", 9.5f),
					Cursor = Cursors.Hand,
					AutoEllipsis = true // запрет переноса названия закладки на 2 ряд и установка "..."
				};
				btnMain.FlatAppearance.BorderSize = 0;
				ApplyContrastHover(btnMain, Color.Transparent, Color.Gainsboro);
				btnMain.Click += async (s, e) => await OpenBookmarkAsync(item);
				// Drag-and-Drop мышкой
				Point dragStart = Point.Empty;
				btnMain.MouseDown += (s, e) =>
				{
					if (e.Button == MouseButtons.Left) dragStart = e.Location;
				};
				btnMain.MouseMove += (s, e) =>
				{
					if (e.Button == MouseButtons.Left &&
					(Math.Abs(e.X - dragStart.X) > 6 || Math.Abs(e.Y - dragStart.Y) > 6))
					{
						btnMain.DoDragDrop(item, DragDropEffects.Move);
					}
				};
				// Контекстное меню
				var ctx = CreateDarkContextMenu();
				ctx.Items.Add("Редактировать (название и ссылка)", null, (s, e) => EditBookmark(item));
				if (!item.IsLocalApp)
				{
					ctx.Items.Add("Сбросить до базовой ссылки (Домой)", null, async (s, e) => await ResetTabToHomeAsync(item));
					ctx.Items.Add("Выгрузить вкладку (освободить ОЗУ)", null, (s, e) => UnloadTab(item.Id));
				}
				ctx.Items.Add(new ToolStripSeparator());
				if (currentIndex > 0)
					ctx.Items.Add("Переместить вверх", null, (s, e) => MoveBookmark(currentIndex, currentIndex - 1));
				if (currentIndex < _currentProfile.Bookmarks.Count - 1)
					ctx.Items.Add("Переместить вниз", null, (s, e) => MoveBookmark(currentIndex, currentIndex + 1));
				ctx.Items.Add(new ToolStripSeparator());
				ctx.Items.Add("Удалить закладку", null, (s, e) => DeleteBookmark(item));
				btnMain.ContextMenuStrip = ctx;
				picIcon.ContextMenuStrip = ctx;
				rowPanel.Controls.Add(btnMain);
				rowPanel.Controls.Add(iconWrap);
				if (_isSidebarCollapsed)
				{
					_toolTip.SetToolTip(picIcon, item.Title);
				}
				// Кнопка "Домой" (⌂) справа в раскрытом виде
				if (!_isSidebarCollapsed && !item.IsLocalApp)
				{
					var btnHome = new Button
					{
						Text = "⌂",
						Width = 32,
						Dock = DockStyle.Right,
						FlatStyle = FlatStyle.Flat,
						BackColor = Color.Transparent,
						ForeColor = Color.DarkGray,
						Font = new Font("Segoe UI", 11, FontStyle.Bold),
						Cursor = Cursors.Hand
					};
					btnHome.FlatAppearance.BorderSize = 0;
					ApplyContrastHover(btnHome, Color.Transparent, Color.DarkGray);
					_toolTip.SetToolTip(btnHome, "Вернуться на базовую сохраненную ссылку");
					btnHome.Click += async (s, e) => await ResetTabToHomeAsync(item);
					rowPanel.Controls.Add(btnHome);
				}
				_sidebarButtons[item.Id] = btnMain;
				_sidebarRows[item.Id] = rowPanel;
				_sidebarIcons[item.Id] = picIcon;
				_bookmarksFlow.Controls.Add(rowPanel);
			}
			UpdateActiveTabHighlight();
		}

		private void UpdateCustomScrollbar()
		{
			if (_bookmarksFlow == null || _customScrollTrack == null) return;

			int contentHeight = 0;
			foreach (Control c in _bookmarksFlow.Controls)
				contentHeight += c.Height + c.Margin.Vertical;

			int viewHeight = _bookmarksFlow.ClientSize.Height;

			if (contentHeight <= viewHeight || viewHeight <= 0)
			{
				_customScrollTrack.Visible = false;
				return;
			}

			_customScrollTrack.Visible = true;
			if (!_customScrollTrack.Visible)
			{
				_customScrollTrack.Visible = true;
				_customScrollTrack.Parent?.PerformLayout();
			}
			int trackHeight = _customScrollTrack.Height;
			int thumbHeight = Math.Max(28, (int)((float)viewHeight / contentHeight * trackHeight));
			_customScrollThumb.Height = thumbHeight;

			int maxScroll = contentHeight - viewHeight;
			int currentScroll = Math.Abs(_bookmarksFlow.AutoScrollPosition.Y);
			int maxThumbTop = trackHeight - thumbHeight;

			if (maxScroll > 0 && maxThumbTop > 0)
			{
				_customScrollThumb.Top = Math.Clamp((int)((float)currentScroll / maxScroll * maxThumbTop), 0, maxThumbTop);
			}
		}


		private void BookmarksFlow_DragDrop(object? sender, DragEventArgs e)
		{
			if (_currentProfile == null || e.Data == null) return;
			if (e.Data.GetData(typeof(BookmarkItem)) is not BookmarkItem draggedItem) return;
			Point clientPoint = _bookmarksFlow.PointToClient(new Point(e.X, e.Y));
			int targetIndex = _currentProfile.Bookmarks.Count - 1;
			for (int i = 0; i < _bookmarksFlow.Controls.Count; i++)
			{
				var ctrl = _bookmarksFlow.Controls[i];
				if (clientPoint.Y <= ctrl.Top + ctrl.Height / 2)
				{
					targetIndex = i;
					break;
				}
			}
			int oldIndex = _currentProfile.Bookmarks.IndexOf(draggedItem);
			if (oldIndex != -1 && oldIndex != targetIndex)
			{
				MoveBookmark(oldIndex, targetIndex);
			}
		}
		private void MoveBookmark(int oldIndex, int newIndex)
		{
			if (_currentProfile == null) return;
			var item = _currentProfile.Bookmarks[oldIndex];
			_currentProfile.Bookmarks.RemoveAt(oldIndex);
			_currentProfile.Bookmarks.Insert(newIndex, item);
			SaveProfileConfig(_currentProfile);
			RenderSidebarBookmarks();
		}
		private async Task ResetTabToHomeAsync(BookmarkItem item)
		{
			if (item.IsLocalApp) return;
			if (_loadedTabs.TryGetValue(item.Id, out var webView))
			{
				webView.Source = new Uri(item.Target);
				if (_activeTabId != item.Id)
				{
					await OpenBookmarkAsync(item);
				}
				else
				{
					_topTitleLabel.Text = item.Title;
					_addressBar.Text = item.Target;
				}
			}
			else
			{
				await OpenBookmarkAsync(item);
			}
		}
		private async Task OpenBookmarkAsync(BookmarkItem item)
		{
			string cleanTarget = item.Target.Trim().Trim('"');
			bool isWebUrl = cleanTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
							cleanTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

			// Если нет http:// или https:// — запускаем как программу Windows и выходим (return)
			if (item.IsLocalApp || !isWebUrl)
			{
				try
				{
					Process.Start(new ProcessStartInfo(cleanTarget) { UseShellExecute = true });
				}
				catch (Exception ex)
				{
					MessageBox.Show($"Ошибка запуска «{cleanTarget}»:\n{ex.Message}");
				}
				return;
			}

			foreach (var wv in _loadedTabs.Values) wv.Visible = false;

			if (!_loadedTabs.TryGetValue(item.Id, out var webView))
			{
				webView = new WebView2 { Dock = DockStyle.Fill };
				_contentPanel.Controls.Add(webView);
				await webView.EnsureCoreWebView2Async(_webEnv);
				webView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
				// Подхватываем и сохраняем иконку сайта напрямую из открытой вкладки WebView2
				webView.CoreWebView2.FaviconChanged += async (s, e) =>
				{
					try
					{
						byte[]? iconBytes = null;
						using var stream = await webView.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
						if (stream != null && stream.Length > 50)
						{
							using var ms = new MemoryStream();
							await stream.CopyToAsync(ms);
							iconBytes = ms.ToArray();
						}

						// Проверяем, читается ли картинка в Bitmap (если сайт отдал SVG — сработает catch и пойдёт рендер через Google)
						Bitmap? cleanBmp = null;
						if (iconBytes != null && iconBytes.Length > 50)
						{
							try
							{
								using var checkMs = new MemoryStream(iconBytes);
								using var tempBmp = new Bitmap(checkMs);
								cleanBmp = new Bitmap(tempBmp);
							}
							catch
							{
								iconBytes = null;
							}
						}

						// Если WebView2 отдал пустоту или нечитаемый SVG — забираем готовый PNG через Google API по домену
						if (cleanBmp == null && Uri.TryCreate(item.Target, UriKind.Absolute, out var uri))
						{
							string fallbackUrl = $"https://google.com{Uri.EscapeDataString($"{uri.Scheme}://{uri.Host}")}";
							iconBytes = await _httpClient.GetByteArrayAsync(fallbackUrl);
							using var checkMs = new MemoryStream(iconBytes);
							using var tempBmp = new Bitmap(checkMs);
							cleanBmp = new Bitmap(tempBmp);
						}

						if (cleanBmp != null && iconBytes != null)
						{
							string cachePath = GetFaviconCachePath(item);
							await File.WriteAllBytesAsync(cachePath, iconBytes);

							if (_sidebarIcons.TryGetValue(item.Id, out var picBox) && !picBox.IsDisposed)
							{
								picBox.Image = cleanBmp;
								picBox.Refresh();
							}
						}
					}
					catch { }
				};

				// Автоматическое подтягивание и кэширование иконки сайта (Favicon) как в Chrome
				webView.CoreWebView2.NewWindowRequested += async (s, e) =>
				{
					var deferral = e.GetDeferral();

					var popupForm = new Form
					{
						Text = "Augur Workspace — Просмотр",
						Size = new Size(1200, 800),
						StartPosition = FormStartPosition.CenterParent,
						BackColor = BgWindow,
						Icon = this.Icon
					};

					try
					{
						int dark = 1;
						DwmSetWindowAttribute(popupForm.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
					}
					catch { }

					var popupWebView = new WebView2 { Dock = DockStyle.Fill };
					popupForm.Controls.Add(popupWebView);
					popupForm.FormClosed += (_, _) => popupWebView.Dispose();

					// Показываем окно, чтобы у контрола создался Handle для WebView2
					popupForm.Show();

					// Инициализируем движок сразу без ожидания события Load
					await popupWebView.EnsureCoreWebView2Async(_webEnv);

					popupWebView.CoreWebView2.DocumentTitleChanged += (_, _) =>
					{
						if (!popupForm.IsDisposed)
							popupForm.Text = $"{popupWebView.CoreWebView2.DocumentTitle} — Augur Workspace";
					};

					e.NewWindow = popupWebView.CoreWebView2;
					deferral.Complete();
				};
				webView.CoreWebView2.SourceChanged += (s, e) =>
				{
					if (_activeTabId == item.Id && !_addressBar.Focused)
					{
						_addressBar.Text = webView.CoreWebView2.Source;
					}
				};
				webView.CoreWebView2.DocumentTitleChanged += (s, e) =>
				{
					string docTitle = webView.CoreWebView2.DocumentTitle;
					if (_activeTabId != item.Id && docTitle.StartsWith("(") && docTitle.Contains(")"))
					{
						string badge = docTitle.Substring(0, docTitle.IndexOf(')') + 1);
						if (_sidebarRows.TryGetValue(item.Id, out var row) &&
						_sidebarButtons.TryGetValue(item.Id, out var b))
						{
							row.BackColor = Color.FromArgb(190, 95, 20);
							b.ForeColor = Color.White;
							if (!_isSidebarCollapsed) b.Text = $"{badge} {item.Title}";
						}
					}
				};
				if (!Uri.TryCreate(cleanTarget, UriKind.Absolute, out var validUri))
				{
					MessageBox.Show($"Некорректный адрес сайта: {cleanTarget}", "Ошибка адреса", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
				webView.Source = validUri;

				_loadedTabs[item.Id] = webView;
			}
			webView.Visible = true;
			_activeTabId = item.Id;
			_topTitleLabel.Text = item.Title;
			_addressBar.Text = webView.CoreWebView2?.Source ?? item.Target;
			_addressBar.Enabled = true;
			_btnSaveBaseUrl.Enabled = true;
			_btnSaveAsNewBookmark.Enabled = true;
			UpdateActiveTabHighlight();
		}
		private void NavigateAddressBar()
		{
			if (_activeTabId == null || !_loadedTabs.TryGetValue(_activeTabId, out var webView)) return;
			string url = _addressBar.Text.Trim();
			if (string.IsNullOrWhiteSpace(url)) return;
			if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
			!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				url = "https://" + url;
			}
			try
			{
				webView.Source = new Uri(url);
				_contentPanel.Focus();
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Некорректный адрес: {ex.Message}");
			}
		}
		private async Task SaveCurrentUrlAsBaseAsync()
		{
			if (_currentProfile == null || _activeTabId == null) return;
			var item = _currentProfile.Bookmarks.FirstOrDefault(b => b.Id == _activeTabId);
			if (item == null || item.IsLocalApp) return;
			string newBaseUrl = _addressBar.Text.Trim();
			if (string.IsNullOrWhiteSpace(newBaseUrl)) return;
			item.Target = newBaseUrl;
			SaveProfileConfig(_currentProfile);
			Color origColor = _btnSaveBaseUrl.BackColor;
			_btnSaveBaseUrl.BackColor = Color.FromArgb(46, 125, 50);
			_btnSaveBaseUrl.ForeColor = Color.White;
			_btnSaveBaseUrl.Text = "✔";
			await Task.Delay(1000);
			_btnSaveBaseUrl.BackColor = origColor;
			_btnSaveBaseUrl.ForeColor = Color.Gainsboro;
			_btnSaveBaseUrl.Text = "💾";
		}

		private async Task SaveCurrentUrlAsNewBookmarkAsync()
		{
			if (_currentProfile == null || _activeTabId == null) return;

			int currentIndex = _currentProfile.Bookmarks.FindIndex(b => b.Id == _activeTabId);
			if (currentIndex == -1) return;

			var currentItem = _currentProfile.Bookmarks[currentIndex];
			string currentUrl = _addressBar.Text.Trim();
			if (string.IsNullOrWhiteSpace(currentUrl)) return;

			// Спрашиваем имя новой закладки (по умолчанию подставляем имя текущей с пометкой)
			string newTitle = PromptSingle("Сохранить как новую закладку", "Название новой закладки:", $"{currentItem.Title} (копия)");
			if (string.IsNullOrWhiteSpace(newTitle)) return;

			var newItem = new BookmarkItem
			{
				Title = newTitle.Trim(),
				Target = currentUrl,
				IsLocalApp = false
			};

			// Вставляем ровно под текущей закладкой
			_currentProfile.Bookmarks.Insert(currentIndex + 1, newItem);
			SaveProfileConfig(_currentProfile);
			RenderSidebarBookmarks();

			// Визуальное подтверждение на кнопке звёздочки
			Color origColor = _btnSaveAsNewBookmark.BackColor;
			_btnSaveAsNewBookmark.BackColor = Color.FromArgb(46, 125, 50);
			_btnSaveAsNewBookmark.ForeColor = Color.White;
			_btnSaveAsNewBookmark.Text = "★";
			await Task.Delay(1000);
			_btnSaveAsNewBookmark.BackColor = origColor;
			_btnSaveAsNewBookmark.ForeColor = Color.Gainsboro;
			_btnSaveAsNewBookmark.Text = "☆";
		}


		private void UpdateActiveTabHighlight()
		{
			foreach (var pair in _sidebarRows)
			{
				bool isCurrent = pair.Key == _activeTabId;
				pair.Value.BackColor = isCurrent ? BgRowActive : BgRowNormal;
				if (isCurrent && _sidebarButtons.TryGetValue(pair.Key, out var btn))
				{
					var bItem = (BookmarkItem)btn.Tag!;
					if (!_isSidebarCollapsed) btn.Text = bItem.Title;
				}
			}
		}
		private void ToggleSidebar()
		{
			_isSidebarCollapsed = !_isSidebarCollapsed;
			_sidebarPanel.Width = _isSidebarCollapsed ? 52 : 255;
			_profileSwitcher.Visible = !_isSidebarCollapsed;
			RenderSidebarBookmarks();
		}
		private void UnloadTab(string id)
		{
			if (_loadedTabs.TryGetValue(id, out var wv))
			{
				wv.Dispose();
				_loadedTabs.Remove(id);
				if (_activeTabId == id)
				{
					_activeTabId = null;
					ResetTopBar();
					UpdateActiveTabHighlight();
				}
			}
		}
		private void SaveProfileConfig(ProfileConfig cfg)
		{
			string dir = Path.Combine(_profilesRoot, cfg.Name);
			Directory.CreateDirectory(dir);
			string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(Path.Combine(dir, "bookmarks.json"), json);
		}
		private void CreateProfileFromLauncher()
		{
			string name = PromptSingle("Новый профиль", "Введите имя профиля:");
			if (!string.IsNullOrWhiteSpace(name))
			{
				SaveProfileConfig(new ProfileConfig { Name = name.Trim() });
				ShowLauncher();
			}
		}

		private void RenameProfile(string oldName)
		{
			string newName = PromptSingle("Переименование профиля", "Введите новое имя профиля:", oldName).Trim();
			if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

			// Проверка на недопустимые символы в имени папки Windows
			if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				MessageBox.Show("Имя профиля содержит недопустимые символы.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			string oldDir = Path.Combine(_profilesRoot, oldName);
			string newDir = Path.Combine(_profilesRoot, newName);

			if (Directory.Exists(newDir))
			{
				MessageBox.Show("Профиль с таким именем уже существует.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			try
			{
				// Переименовываем папку профиля
				Directory.Move(oldDir, newDir);

				// Обновляем поле Name внутри файла bookmarks.json
				string cfgPath = Path.Combine(newDir, "bookmarks.json");
				if (File.Exists(cfgPath))
				{
					var cfg = (ProfileConfig)JsonSerializer.Deserialize(File.ReadAllText(cfgPath), typeof(ProfileConfig))!;
					cfg.Name = newName;
					SaveProfileConfig(cfg);
				}

				ShowLauncher();
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Не удалось переименовать профиль:\n{ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private void DeleteProfile(string profileName)
		{
			var confirm = MessageBox.Show(
				$"Точно удалить профиль «{profileName}» со всеми закладками, иконками и сессиями?\nЭто действие нельзя отменить.",
				"Удаление профиля",
				MessageBoxButtons.YesNo,
				MessageBoxIcon.Warning
			);

			if (confirm != DialogResult.Yes) return;

			try
			{
				string profDir = Path.Combine(_profilesRoot, profileName);
				if (Directory.Exists(profDir))
				{
					Directory.Delete(profDir, recursive: true);
				}
				ShowLauncher();
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Не удалось удалить папку профиля:\n{ex.Message}", "Ошибка удаления", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}


		private async void CreateProfileFromSwitcher()
		{
			string name = PromptSingle("Новый профиль", "Введите имя профиля:");
			if (!string.IsNullOrWhiteSpace(name))
			{
				string cleanName = name.Trim();
				SaveProfileConfig(new ProfileConfig { Name = cleanName });
				await SwitchProfileAsync(cleanName);
			}
			else if (_currentProfile != null)
			{
				RefreshProfileSwitcher(_currentProfile.Name);
			}
		}
		private void AddNewBookmarkDialog()
		{
			if (_currentProfile == null) return;
			var res = PromptBookmark("Новая закладка", "Новая закладка", "https://");
			if (res == null) return;
			var (normalized, isLocal) = NormalizeTarget(res.Value.target);
			_currentProfile.Bookmarks.Add(new BookmarkItem
			{
				Title = res.Value.title,
				Target = normalized,
				IsLocalApp = isLocal
			});
			SaveProfileConfig(_currentProfile);
			RenderSidebarBookmarks();
		}
		private void EditBookmark(BookmarkItem item)
		{
			if (_currentProfile == null) return;
			var res = PromptBookmark("Редактировать закладку", item.Title, item.Target);
			if (res == null) return;

			var (normalized, isLocal) = NormalizeTarget(res.Value.target);
			bool urlChanged = item.Target != normalized;
			item.Title = res.Value.title;
			item.Target = normalized;
			item.IsLocalApp = isLocal;

			if (urlChanged)
			{
				try { File.Delete(GetFaviconCachePath(item)); } catch { }
			}
			SaveProfileConfig(_currentProfile);
			RenderSidebarBookmarks();
			if (_activeTabId == item.Id)
			{
				_topTitleLabel.Text = item.Title;
				if (urlChanged && !item.IsLocalApp && _loadedTabs.TryGetValue(item.Id, out var wv))
				{
					wv.Source = new Uri(item.Target);
					_addressBar.Text = item.Target;
				}
			}
		}
		private void DeleteBookmark(BookmarkItem item)
		{
			if (_currentProfile == null) return;
			UnloadTab(item.Id);
			try { File.Delete(GetFaviconCachePath(item)); } catch { }
			_currentProfile.Bookmarks.Remove(item);
			SaveProfileConfig(_currentProfile);
			RenderSidebarBookmarks();
		}
		private static string PromptSingle(string caption, string text, string defaultVal = "")
		{
			Form prompt = new Form()
			{
				Width = 420,
				Height = 160,
				FormBorderStyle = FormBorderStyle.FixedDialog,
				Text = caption,
				StartPosition = FormStartPosition.CenterParent,
				BackColor = BgSidebar,
				ForeColor = Color.White
			};
			ApplyDarkTitleBar(prompt.Handle);
			Label textLabel = new Label() { Left = 15, Top = 15, Width = 370, Text = text };
			TextBox textBox = new TextBox() { Left = 15, Top = 42, Width = 370, Text = defaultVal, BackColor = BgRowNormal, ForeColor = Color.White };
			Button confirmation = new Button() { Text = "ОК", Left = 285, Width = 100, Top = 78, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = BgRowActive, ForeColor = Color.White };
			ApplyContrastHover(confirmation, BgRowActive, Color.White);
			prompt.Controls.Add(textBox);
			prompt.Controls.Add(confirmation);
			prompt.Controls.Add(textLabel);
			prompt.AcceptButton = confirmation;
			return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : "";
		}
		private static (string title, string target)? PromptBookmark(string caption, string defaultTitle, string defaultTarget)
		{
			Form prompt = new Form()
			{
				Width = 480,
				Height = 235,
				FormBorderStyle = FormBorderStyle.FixedDialog,
				Text = caption,
				StartPosition = FormStartPosition.CenterParent,
				BackColor = BgSidebar,
				ForeColor = Color.White
			};
			ApplyDarkTitleBar(prompt.Handle);
			Label lblTitle = new Label() { Left = 15, Top = 15, Width = 430, Text = "Название закладки:" };
			TextBox txtTitle = new TextBox() { Left = 15, Top = 38, Width = 430, Text = defaultTitle, BackColor = BgRowNormal, ForeColor = Color.White };
			Label lblTarget = new Label() { Left = 15, Top = 75, Width = 430, Text = "Ссылка (https://...) или путь к локальному файлу (.exe, .xlsx):" };
			TextBox txtTarget = new TextBox() { Left = 15, Top = 98, Width = 430, Text = defaultTarget, BackColor = BgRowNormal, ForeColor = Color.White };
			Button btnOk = new Button() { Text = "Сохранить", Left = 345, Width = 100, Top = 145, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = BgRowActive, ForeColor = Color.White };
			ApplyContrastHover(btnOk, BgRowActive, Color.White);
			prompt.Controls.AddRange(new Control[] { lblTitle, txtTitle, lblTarget, txtTarget, btnOk });
			prompt.AcceptButton = btnOk;
			if (prompt.ShowDialog() == DialogResult.OK &&
			!string.IsNullOrWhiteSpace(txtTitle.Text) &&
			!string.IsNullOrWhiteSpace(txtTarget.Text))
			{
				return (txtTitle.Text.Trim(), txtTarget.Text.Trim());
			}
			return null;
		}
	}
	public class SmoothScrollFlowPanel : FlowLayoutPanel
	{
		public event Action? Scrolled;

		protected override void WndProc(ref Message m)
		{
			base.WndProc(ref m);
			// 0x0115 = WM_VSCROLL, 0x020A = WM_MOUSEWHEEL
			if (m.Msg == 0x0115 || m.Msg == 0x020A)
			{
				Scrolled?.Invoke();
			}
		}
	}

	public class DarkMenuRenderer : ToolStripProfessionalRenderer
	{
		public DarkMenuRenderer() : base(new DarkMenuColors()) { }

		protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
		{
			e.TextColor = e.Item.Selected ? Color.FromArgb(18, 19, 22) : Color.Gainsboro;
			base.OnRenderItemText(e);
		}

		protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
		{
			e.ArrowColor = e.Item != null && e.Item.Selected ? Color.FromArgb(18, 19, 22) : Color.Gainsboro;
			base.OnRenderArrow(e);
		}
	}

	public class DarkMenuColors : ProfessionalColorTable
	{
		public override Color MenuItemSelected => Color.FromArgb(228, 230, 235);
		public override Color MenuItemSelectedGradientBegin => Color.FromArgb(228, 230, 235);
		public override Color MenuItemSelectedGradientEnd => Color.FromArgb(228, 230, 235);
		public override Color MenuItemBorder => Color.Transparent;
		public override Color MenuBorder => Color.FromArgb(55, 58, 66);
		public override Color ToolStripDropDownBackground => Color.FromArgb(30, 31, 34);
		public override Color ImageMarginGradientBegin => Color.FromArgb(30, 31, 34);
		public override Color ImageMarginGradientMiddle => Color.FromArgb(30, 31, 34);
		public override Color ImageMarginGradientEnd => Color.FromArgb(30, 31, 34);
		public override Color SeparatorDark => Color.FromArgb(55, 58, 66);
		public override Color SeparatorLight => Color.Transparent;
	}


}