using System.Collections.Generic;

namespace AugurWorkspace
{
	public class BookmarkItem
	{
		public string Id { get; set; } = System.Guid.NewGuid().ToString();
		public string Title { get; set; } = "Новая закладка";
		public string Target { get; set; } = "https://pathofexile.com"; // URL или путь к .exe/.xlsx
		public bool IsLocalApp { get; set; } = false; // false = вкладка WebView2, true = запуск локального файла
	}

	public class ProfileConfig
	{
		public string Name { get; set; } = "Default";
		public List<BookmarkItem> Bookmarks { get; set; } = new List<BookmarkItem>();
	}
}
