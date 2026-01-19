    using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Central enemy dialogue controller with a lightweight queue, portrait float-in and letter-by-letter reveal.
/// Backwards-compatible Speak(...) API retained.
/// </summary>
[DisallowMultipleComponent]
public class EnemyDialogueController : MonoBehaviour
{
    public static EnemyDialogueController Instance { get; private set; }

    [Header("UI (assign in inspector)")]
    [Tooltip("Root GameObject of the dialogue UI. The script will enable/disable this object.")]
    public GameObject dialogueObject;

    [Tooltip("TextMeshProUGUI used for the message.")]
    public TextMeshProUGUI messageText;

    // public Image portraitImage; // Removed - portrait is now part of dialogueObject

    // public RectTransform portraitTransform; // Removed - now auto-found from portrait image

    // Get portrait image from dialogue object
    private Image PortraitImage => dialogueObject?.GetComponentInChildren<Image>();

    // Get portrait transform from portrait image
    private RectTransform PortraitTransform => PortraitImage?.rectTransform;

    // public Sprite kingPortrait; // Removed - king portrait should be set on the dialogue object

    [Header("Timing / animation")]
    public float defaultDuration = 3.0f;
    [Tooltip("Seconds for the portrait float-in animation.")]
    public float portraitFloatDuration = 0.32f;
    [Tooltip("Start offset (local anchored) for the portrait animation.")]
    public Vector2 portraitStartOffset = new Vector2(0f, -42f);
    [Tooltip("Delay per-character when revealing text.")]
    public float letterDelay = 0.03f;
    [Tooltip("Optional minimum time a message should remain on screen (prevents too quick flicker).")]
    public float minDisplayTime = 0.6f;

    // internal queue
    struct DialogMessage { public string text; public Sprite portrait; public float duration; }
    readonly Queue<DialogMessage> queue = new Queue<DialogMessage>();

    Coroutine workerCoroutine;

    // convenience arrays used by legacy callers
    readonly string[] firstContactLines = new[]
    {
        "Na, išdrįsai pasirodyti. Kas tu toks?",
        "Sveikas. Aš esu Karalius — geriau būk mandagus.",
        "Atėjai į mano žemę. Papasakok kodėl — greitai."
    };

    readonly string[] engagedLines = new[]
    {
        "Jie atėjo — pasiruoškite mūšiui!",
        "Į priekį — parodyk savo jėgą!",
        "Nepaleiskite iniciatyvos."
    };

    readonly string[] buildingDestroyedLines = new[]
    {
        "Tai brangiai kainuos.",
        "Atsakysi už tai."
    };

    readonly string[] unitsKilledLines = new[]
    {
        "Vienas nužudytas.",
        "Nedidelė grupė sunaikinta.",
        "Didelis desantas atsidūrė po žeme."
    };

    readonly string[] attackingWorkersLines = new[]
    {
        "Gynyba! Darbininkai puolami!",
        "Neleiskite jiems skriausti mano darbininkų!"
    };

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        if (dialogueObject != null)
            dialogueObject.SetActive(false);
        if (PortraitImage != null)
            PortraitImage.enabled = false;
    }

    // Core display API used by other game code --------------------------------

    /// <summary>
    /// Backwards-compatible Speak. Enqueues the message for sequential display.
    /// </summary>
    public void Speak(string message, Sprite portrait = null, float duration = -1f)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (duration <= 0f) duration = defaultDuration;

        // Avoid exact duplicates already queued (simple de-dup to reduce spam)
        foreach (var m in queue)
            if (m.text == message) return;

        queue.Enqueue(new DialogMessage { text = message, portrait = portrait, duration = duration });
        if (workerCoroutine == null)
            workerCoroutine = StartCoroutine(ProcessQueue());
    }

    public void SpeakFirstContact(Sprite portrait = null)
    {
        string s = firstContactLines[Random.Range(0, firstContactLines.Length)];
        Speak(s, portrait, defaultDuration);
    }

    public void SpeakBuildingDestroyed(string buildingName)
    {
        string s = $"{buildingName} sunaikinta.";
        if (Random.value > 0.4f)
            s = buildingDestroyedLines[Random.Range(0, buildingDestroyedLines.Length)];
        Speak(s, null, defaultDuration);
    }

    public void SpeakAttackingWorkers()
    {
        string s = attackingWorkersLines[Random.Range(0, attackingWorkersLines.Length)];
        Speak(s, null, defaultDuration);
    }

    public void SpeakUnitsKilled(int count)
    {
        string s;
        if (count <= 1) s = unitsKilledLines[0];
        else if (count < 5) s = unitsKilledLines[1];
        else s = unitsKilledLines[2];
        Speak(s, null, defaultDuration);
    }

    public void SpeakEngaged()
    {
        string s = engagedLines[Random.Range(0, engagedLines.Length)];
        Speak(s, null, defaultDuration * 0.9f);
    }

    // Queue processor --------------------------------------------------------

    IEnumerator ProcessQueue()
    {
        while (queue.Count > 0)
        {
            var msg = queue.Dequeue();
            yield return StartCoroutine(DisplayMessageCoroutine(msg.text, msg.portrait, msg.duration));
        }

        workerCoroutine = null;
    }

    IEnumerator DisplayMessageCoroutine(string message, Sprite portrait, float duration)
    {
        if (duration < minDisplayTime) duration = minDisplayTime;

        // show root
        if (dialogueObject != null)
            dialogueObject.SetActive(true);

        // portrait setup + float-in
        if (PortraitImage != null)
        {
            if (portrait != null)
            {
                PortraitImage.sprite = portrait;
                PortraitImage.enabled = true;

                if (PortraitTransform != null)
                {
                    Vector2 target = PortraitTransform.anchoredPosition;
                    Vector2 start = target + portraitStartOffset;
                    PortraitTransform.anchoredPosition = start;

                    float t = 0f;
                    while (t < portraitFloatDuration)
                    {
                        t += Time.deltaTime;
                        float p = Mathf.Clamp01(t / portraitFloatDuration);
                        // smooth step
                        PortraitTransform.anchoredPosition = Vector2.Lerp(start, target, Mathf.SmoothStep(0f, 1f, p));
                        yield return null;
                    }
                    PortraitTransform.anchoredPosition = target;
                }
            }
            else
            {
                PortraitImage.enabled = false;
            }
        }

        // reveal text letter-by-letter
        if (messageText != null)
        {
            messageText.text = string.Empty;
            for (int i = 0; i < message.Length; i++)
            {
                messageText.text += message[i];
                yield return new WaitForSeconds(letterDelay);
            }
        }

        // hold on screen for duration
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        // hide portrait and dialogue root (simple instant hide to avoid long exit animations)
        if (PortraitImage != null)
            PortraitImage.enabled = false;
        if (dialogueObject != null)
            dialogueObject.SetActive(false);
    }

    /// <summary>
    /// Immediately clear queue and hide UI.
    /// </summary>
    public void Hide()
    {
        queue.Clear();

        if (workerCoroutine != null)
        {
            StopCoroutine(workerCoroutine);
            workerCoroutine = null;
        }

        if (dialogueObject != null)
            dialogueObject.SetActive(false);
        if (PortraitImage != null)
            PortraitImage.enabled = false;
        if (displayCoroutine != null)
        {
            StopCoroutine(displayCoroutine);
            displayCoroutine = null;
        }
    }

    // internal: support canceling single reveal coroutine if needed
    Coroutine displayCoroutine;
}