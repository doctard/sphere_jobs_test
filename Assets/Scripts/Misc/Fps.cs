/// https://gist.github.com/mstevenson/5103365#file-fps-cs
using UnityEngine;
using System.Collections;

public class Fps : MonoBehaviour
{
    public Color color;
    private float count;

    private IEnumerator Start()
    {
        GUI.depth = 2;
        while (true)
        {
            count = 1f / Time.unscaledDeltaTime;
            yield return new WaitForSeconds(0.1f);
        }
    }

    private void OnGUI()
    {
        Color c = GUI.color;
        GUI.color = color;
        GUI.Label(new Rect(5, 40, 100, 25), "FPS: " + Mathf.Round(count));
        GUI.color = c;
    }
}