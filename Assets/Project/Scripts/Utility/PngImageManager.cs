using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class PngImageManager : MonoBehaviour
{
    [SerializeField] private InputField _urlField;
    [SerializeField] private Button _downLoadButton;
    [SerializeField] private ImagePreview _previewPrefab;
    [SerializeField] private RectTransform _parent;
    [SerializeField] private PngParserExecutor _executor;

    private Encoding _latin1 = Encoding.GetEncoding(28591);

    private static string SaveDirectory => Application.persistentDataPath;

    private void Awake()
    {
        LoadAllImages();

        _downLoadButton.onClick.AddListener(() => { DownloadAsync(_urlField.text).Forget(); });
    }

    public void LoadAllImages()
    {
        string[] files = Directory.GetFiles(SaveDirectory);

        foreach (string file in files)
        {
            string path = Path.Combine(SaveDirectory, file);
            ImagePreview preview = Instantiate(_previewPrefab, _parent);
            preview.LoadImage(path);
            preview.OnClicked += filePath => _executor.LoadImage(filePath);
        }
    }

    public static async UniTask<Texture2D> DownloadAsync(string url)
    {
        using UnityWebRequest req = UnityWebRequestTexture.GetTexture(url);

        await req.SendWebRequest();

        Texture2D texture = DownloadHandlerTexture.GetContent(req);

        string savePath = GetSavePath(url);

        Debug.Log($"Save a texture to [{savePath}]");

        byte[] data = texture.EncodeToPNG();
        File.WriteAllBytes(savePath, data);

        return texture;
    }

    public static string GetSavePath(string url)
    {
        byte[] binary = Encoding.UTF8.GetBytes(url);
        MD5CryptoServiceProvider csp = new MD5CryptoServiceProvider();
        byte[] hashBytes = csp.ComputeHash(binary);
        StringBuilder builder = new StringBuilder();
        foreach (var b in hashBytes)
        {
            builder.Append(b.ToString("x2"));
        }

        string filename = builder.ToString();

        return Path.Combine(SaveDirectory, filename);
    }
}