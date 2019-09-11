using System.IO;

namespace Assets.NuGet.Editor
{
	public static class FileSystemHelpers
	{
		public static void Copy(string sourceDirectory, string targetDirectory)
		{
			CopyAll(new DirectoryInfo(sourceDirectory), new DirectoryInfo(targetDirectory));
		}

		public static void CopyAll(DirectoryInfo source, DirectoryInfo target)
		{
			Directory.CreateDirectory(target.FullName);

			// Copy each file into the new directory.
			foreach (var file in source.EnumerateFiles())
			{
				if(File.Exists(Path.Combine(target.FullName, file.Name)))
					File.SetAttributes(Path.Combine(target.FullName, file.Name), FileAttributes.Normal);

				file.CopyTo(Path.Combine(target.FullName, file.Name), true);
			}

			foreach (var subDirectory in source.EnumerateDirectories())
			{
				var nextTargetSubDir = target.CreateSubdirectory(subDirectory.Name);
				CopyAll(subDirectory, nextTargetSubDir);
			}
		}
	}
}
