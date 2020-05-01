﻿using System.Collections.Generic;
using System.Linq;
using UniInject;
using UniRx;
using UnityEngine;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

public class PlayerController : MonoBehaviour, INeedInjection
{
    [InjectedInInspector]
    public PlayerUiController playerUiControllerPrefab;

    public PlayerProfile PlayerProfile { get; private set; }
    public MicProfile MicProfile { get; private set; }

    [Inject(searchMethod = SearchMethods.GetComponentInChildren)]
    public PlayerNoteRecorder PlayerNoteRecorder { get; private set; }

    [Inject(searchMethod = SearchMethods.GetComponentInChildren)]
    public PlayerPitchTracker PlayerPitchTracker { get; private set; }

    [Inject(searchMethod = SearchMethods.GetComponentInChildren)]
    public MicSampleRecorder MicSampleRecorder { get; private set; }

    [Inject(searchMethod = SearchMethods.GetComponentInChildren)]
    public PlayerScoreController PlayerScoreController { get; private set; }

    private Voice voice;
    public Voice Voice
    {
        get
        {
            return voice;
        }
        private set
        {
            voice = value;
            SortedSentences = voice.Sentences.ToList();
            SortedSentences.Sort(Sentence.comparerByStartBeat);
        }
    }

    // The sorted sentences of the Voice
    public List<Sentence> SortedSentences { get; private set; } = new List<Sentence>();

    [Inject]
    private Injector injector;

    // An injector with additional bindings, such as the PlayerProfile and the MicProfile.
    private Injector childrenInjector;

    [Inject]
    private PlayerUiArea playerUiArea;

    // The PlayerUiController is instantiated by the PlayerController as a child of the PlayerUiArea.
    private PlayerUiController playerUiController;

    [Inject]
    private SongMeta songMeta;

    private int displaySentenceIndex;

    private LyricsDisplayer lyricsDisplayer;
    public LyricsDisplayer LyricsDisplayer
    {
        get
        {
            return lyricsDisplayer;
        }
        set
        {
            lyricsDisplayer = value;
            UpdateLyricsDisplayer(GetSentence(displaySentenceIndex), GetSentence(displaySentenceIndex + 1));
        }
    }

    public void Init(PlayerProfile playerProfile, string voiceName, MicProfile micProfile)
    {
        this.PlayerProfile = playerProfile;
        this.MicProfile = micProfile;
        this.Voice = GetVoice(songMeta, voiceName);
        this.playerUiController = Instantiate(playerUiControllerPrefab, playerUiArea.transform);
        this.childrenInjector = CreateChildrenInjectorWithAdditionalBindings();

        // Inject all
        childrenInjector.InjectAllComponentsInChildren(this);
        childrenInjector.InjectAllComponentsInChildren(playerUiController);

        // Init instances
        playerUiController.Init(PlayerProfile, MicProfile);
        PlayerScoreController.Init(Voice);

        SetDisplaySentenceIndex(0);
    }

    private Injector CreateChildrenInjectorWithAdditionalBindings()
    {
        Injector newInjector = UniInjectUtils.CreateInjector(injector);
        newInjector.AddBindingForInstance(PlayerProfile);
        newInjector.AddBindingForInstance(MicProfile);
        newInjector.AddBindingForInstance(MicSampleRecorder);
        newInjector.AddBindingForInstance(PlayerPitchTracker);
        newInjector.AddBindingForInstance(PlayerNoteRecorder);
        newInjector.AddBindingForInstance(PlayerScoreController);
        newInjector.AddBindingForInstance(playerUiController);
        newInjector.AddBindingForInstance(newInjector);
        newInjector.AddBindingForInstance(this);
        return newInjector;
    }

    public void SetCurrentBeat(double currentBeat)
    {
        // Change the current display sentence, when the current beat is over its last note.
        if (displaySentenceIndex < SortedSentences.Count && currentBeat >= GetDisplaySentence().LinebreakBeat)
        {
            Sentence nextDisplaySentence = GetUpcomingSentenceForBeat(currentBeat);
            int nextDisplaySentenceIndex = SortedSentences.IndexOf(nextDisplaySentence);
            if (nextDisplaySentenceIndex >= 0)
            {
                SetDisplaySentenceIndex(nextDisplaySentenceIndex);
            }
        }
    }

    private Voice GetVoice(SongMeta songMeta, string voiceName)
    {
        IReadOnlyCollection<Voice> voices = songMeta.GetVoices();
        if (string.IsNullOrEmpty(voiceName) || voiceName == Voice.soloVoiceName)
        {
            Voice mergedVoice = CreateMergedVoice(voices);
            return mergedVoice;
        }
        else
        {
            Voice matchingVoice = voices.Where(it => it.Name == voiceName).FirstOrDefault();
            if (matchingVoice != null)
            {
                return matchingVoice;
            }
            else
            {
                string voiceNameCsv = voices.Select(it => it.Name).ToCsv();
                throw new UnityException($"The song data does not contain a voice with name {voiceName}."
                    + $" Available voices: {voiceNameCsv}");
            }
        }
    }

    private Voice CreateMergedVoice(IReadOnlyCollection<Voice> voices)
    {
        if (voices.Count == 1)
        {
            return voices.First();
        }

        Voice mergedVoice = new Voice("");
        List<Sentence> allSentences = voices.SelectMany(voice => voice.Sentences).ToList();
        List<Note> allNotes = allSentences.SelectMany(sentence => sentence.Notes).ToList();
        // Sort notes by start beat
        allNotes.Sort((note1, note2) => note1.StartBeat.CompareTo(note2.StartBeat));
        // Find sentence borders
        List<int> lineBreaks = allSentences.Select(sentence => sentence.LinebreakBeat).Where(lineBreak => lineBreak > 0).ToList();
        lineBreaks.Sort();
        int lineBreakIndex = 0;
        int nextLineBreakBeat = lineBreaks[lineBreakIndex];
        // Create sentences
        Sentence mutableSentence = new Sentence();
        foreach (Note note in allNotes)
        {
            if (!mutableSentence.Notes.IsNullOrEmpty()
                && (nextLineBreakBeat >= 0 && note.StartBeat > nextLineBreakBeat))
            {
                // Finish the last sentence
                mutableSentence.SetLinebreakBeat(nextLineBreakBeat);
                mergedVoice.AddSentence(mutableSentence);
                mutableSentence = new Sentence();

                lineBreakIndex++;
                if (lineBreakIndex < lineBreaks.Count)
                {
                    nextLineBreakBeat = lineBreaks[lineBreakIndex];
                }
                else
                {
                    lineBreakIndex = -1;
                }
            }
            mutableSentence.AddNote(note);
        }

        // Finish the last sentence
        mergedVoice.AddSentence(mutableSentence);
        return mergedVoice;
    }

    private void SetDisplaySentenceIndex(int newValue)
    {
        displaySentenceIndex = newValue;

        Sentence current = GetSentence(displaySentenceIndex);
        Sentence next = GetSentence(displaySentenceIndex + 1);

        // Update the UI
        playerUiController.DisplaySentence(current);
        UpdateLyricsDisplayer(current, next);
    }

    private void UpdateLyricsDisplayer(Sentence current, Sentence next)
    {
        if (lyricsDisplayer == null)
        {
            return;
        }

        lyricsDisplayer.SetCurrentSentence(current);
        lyricsDisplayer.SetNextSentence(next);
    }

    public Sentence GetSentence(int index)
    {
        Sentence sentence = (index >= 0 && index < SortedSentences.Count) ? SortedSentences[index] : null;
        return sentence;
    }

    public double GetNextStartBeat(double currentBeat)
    {
        Sentence displaySentence = GetDisplaySentence();
        if (displaySentence == null)
        {
            return -1d;
        }

        // It is possible that the display is still shown only because the LinebreakBeat is after the MaxBeat.
        if (displaySentence.MaxBeat < currentBeat)
        {
            // Use the beat of the following sentence
            Sentence nextDisplaySentence = GetSentence(displaySentenceIndex + 1);
            if (nextDisplaySentence != null)
            {
                return nextDisplaySentence.MinBeat;
            }
        }

        return displaySentence.MinBeat;
    }

    public Sentence GetUpcomingSentenceForBeat(double currentBeat)
    {
        Sentence result = Voice.Sentences
            .Where(sentence => currentBeat < sentence.LinebreakBeat)
            .FirstOrDefault();
        return result;
    }

    public Sentence GetDisplaySentence()
    {
        return GetSentence(displaySentenceIndex);
    }

    public Note GetLastNoteInSong()
    {
        return SortedSentences.Last().Notes.OrderBy(note => note.EndBeat).Last();
    }
}
