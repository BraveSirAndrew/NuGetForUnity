using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Assets.NuGet.Editor
{
	public static class ImageLoader
	{
		private static Dictionary<string, Texture2D> _inMemoryCache = new Dictionary<string, Texture2D>();

		public static Texture LoadImageAsync(string imageUrl, Texture2D defaultIcon, EditorWindow editor)
		{
			if (string.IsNullOrEmpty(imageUrl))
			{
				Debug.LogWarning($"imageUrl is null or empty");
				return defaultIcon;
			}

			if (_inMemoryCache.TryGetValue(imageUrl, out var image))
				return image;

			_inMemoryCache.Add(imageUrl, defaultIcon);

			EditorCoroutineUtility.StartCoroutine(LoadImageInternalAsync(imageUrl, editor), editor);
			return defaultIcon;
		}

		private static IEnumerator LoadImageInternalAsync(string imageUrl, EditorWindow editor)
		{
			// if the image exists on disk already, load it from there instead
			var url = imageUrl;
			if (ExistsInDiskCache(imageUrl))
			{
				url = "file:///" + GetFilePath(imageUrl);
			}

			using (var uwr = UnityWebRequestTexture.GetTexture(url))
			{
				yield return uwr.SendWebRequest();

				if (uwr.isNetworkError || uwr.isHttpError)
				{
					Debug.LogError($"Couldn't download image {url}: {uwr.error}");
					yield break;
				}

				while (uwr.isDone == false)
					yield return null;
			
				_inMemoryCache[imageUrl] = DownloadHandlerTexture.GetContent(uwr);
				CacheTextureOnDisk(imageUrl, uwr.downloadHandler.data);
			}

			editor.Repaint();
		}

		private static void CacheTextureOnDisk(string url, byte[] bytes)
		{
			string diskPath = GetFilePath(url);
			File.WriteAllBytes(diskPath, bytes);
		}

		private static bool ExistsInDiskCache(string url)
		{
			return File.Exists(GetFilePath(url));
		}

		private static string GetFilePath(string url)
		{
			return Path.Combine(Application.temporaryCachePath, GetHash(url));
		}

		private static string GetHash(string s)
		{
			if (string.IsNullOrEmpty(s))
				return null;

			var md5 = MD5.Create();
			var data = md5.ComputeHash(Encoding.Default.GetBytes(s));
			var sBuilder = new StringBuilder();
			for (var i = 0; i < data.Length; i++)
			{
				sBuilder.Append(data[i].ToString("x2"));
			}

			return sBuilder.ToString();
		}
	}
}