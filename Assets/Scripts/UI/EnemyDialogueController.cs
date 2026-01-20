using System.Collections.Generic;
using TMPro;
using UnityEngine;
using System.Collections;
using System.Linq;

/// <summary>
/// Central enemy dialogue controller with a lightweight queue.
/// Backwards-compatible Speak(...) API retained (portrait parameter is ignored).
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

    [Header("Timing")]
    public float defaultDuration = 3.0f;
    [Tooltip("Delay per-character when revealing text.")]
    public float letterDelay = 0.03f;
    [Tooltip("Optional minimum time a message should remain on screen (prevents too quick flicker).")]
    public float minDisplayTime = 0.6f;

    // internal queue (portrait is intentionally NOT tracked)
    struct DialogMessage { public string text; public float duration; }
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

        // Attempt to find children even if dialogueObject is disabled in scene
        TryCacheUiRefs();
    }

    private void Start()
    {
        // Keep hidden by default (safe even if already disabled)
        if (dialogueObject != null)
            dialogueObject.SetActive(false);

        // Ensure we have references to messageText (handles disabled dialogueObject case)
        TryCacheUiRefs();
    }

    // Try to locate messageText even if dialogueObject is disabled in scene.
    // Use GetComponentsInChildren(includeInactive:true) to find children on disabled GameObject.
    void TryCacheUiRefs()
    {
        if (dialogueObject != null)
        {
            if (messageText == null)
            {
                messageText = dialogueObject.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault();
                if (messageText == null)
                    Debug.LogWarning("[EnemyDialogue] messageText not assigned and could not be found under dialogueObject.");
            }
        }
        else
        {
            Debug.LogWarning("[EnemyDialogue] dialogueObject not assigned in inspector. Assign scene UI root for dialogue.");
        }
    }

    // Core display API used by other game code --------------------------------

    /// <summary>
    /// Backwards-compatible Speak. Enqueues the message for sequential display.
    /// The optional portrait parameter is accepted for API compatibility but intentionally ignored;
    /// the portrait is considered part of the dialogueObject and is not modified here.
    /// </summary>
    public void Speak(string message, Sprite portrait = null, float duration = -1f)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (duration <= 0f) duration = defaultDuration;

        // Avoid exact duplicates already queued (simple de-dup to reduce spam)
        foreach (var m in queue)
            if (m.text == message) return;

        // Debug trace so it's easy to see Speak calls when dialogueObject is disabled
        Debug.Log($"[EnemyDialogue] Enqueue message: \"{message}\" (dialogueObject assigned: {dialogueObject != null}, messageText assigned: {messageText != null})");

        // Note: portrait is intentionally NOT stored or modified here.
        queue.Enqueue(new DialogMessage { text = message, duration = duration });
        if (workerCoroutine == null)
        {
            workerCoroutine = StartCoroutine(ProcessQueue());
        }
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
        Debug.Log($"[EnemyDialogue] ProcessQueue started (queued items: {queue.Count})");
        while (queue.Count > 0)
        {
            var msg = queue.Dequeue();
            yield return StartCoroutine(DisplayMessageCoroutine(msg.text, msg.duration));
        }

        workerCoroutine = null;
        Debug.Log("[EnemyDialogue] ProcessQueue finished");
    }

    IEnumerator DisplayMessageCoroutine(string message, float duration)
    {
        if (duration < minDisplayTime) duration = minDisplayTime;

        // Ensure UI refs in case they were not cached earlier
        TryCacheUiRefs();

        // show root
        if (dialogueObject != null)
            dialogueObject.SetActive(true);
        else
        {
            Debug.LogWarning("[EnemyDialogue] dialogueObject is null when trying to display message.");
            yield break;
        }

        // reveal text letter-by-letter (portrait is not handled here; it's part of dialogueObject)
        if (messageText != null)
        {
            messageText.text = string.Empty;
            for (int i = 0; i < message.Length; i++)
            {
                messageText.text += message[i];
                yield return new WaitForSeconds(letterDelay);
            }
        }
        else
        {
            Debug.LogWarning("[EnemyDialogue] messageText is not assigned, cannot display text.");
        }

        // hold on screen for duration
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        // hide dialogue root only; do not touch portrait Image component
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
    }
}