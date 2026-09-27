// Synthetic upstream only. Serve/kernel still construct and execute the four real
// system media tools and original /v2 audio routes. No model or tool-loop emulation.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

export function nativeMediaPlatform({ directory }) {
  const asset = name => readFileSync(new URL(`../Tansr.Sdk.Windows.Tests/Hosting/Fixtures/Media/${name}`, import.meta.url));
  const png = asset('blue.png'), mp4 = asset('blue.mp4'), wav = asset('silence.wav');
  const fixtureWaveSha256 = createHash('sha256').update(wav).digest('hex');
  const image = `data:image/png;base64,${png.toString('base64')}`;
  const video = `data:video/mp4;base64,${mp4.toString('base64')}`;
  const audio = `data:audio/wav;base64,${wav.toString('base64')}`;
  const mediaRecords = [];
  const bundleExtra = {
    platformModels: {
      imageGen: [{ model: 'native-image', displayName: 'Synthetic image', outputHosting: 'inline' }],
      videoGen: [{ model: 'native-video', displayName: 'Synthetic video', outputHosting: 'inline' }],
      speechToText: [{ model: 'native-asr', displayName: 'Synthetic transcription', outputHosting: 'inline' }],
      textToSpeech: [{ model: 'native-tts', displayName: 'Synthetic speech', outputHosting: 'inline',
        constraints: { maxChars: 16, voices: [{ id: 'native-voice', label: 'Synthetic voice' }], defaultVoice: 'native-voice', formats: ['wav'], inputLimit: { unit: 'unicode_code_points', max: 16 } } }],
    },
  };
  const toolRequests = {
    UI_MEDIA_IMAGE: { name: 'ImageGen', args: { model: 'native-image', prompt: 'Synthetic blue square' } },
    UI_MEDIA_BAD_IMAGE: { name: 'ImageGen', args: { model: 'native-image', prompt: 'Synthetic bad image' } },
    UI_MEDIA_VIDEO: { name: 'VideoGen', args: { model: 'native-video', prompt: 'Synthetic blue frame', duration: 2 } },
    UI_MEDIA_SPEAK: { name: 'TextToSpeech', args: { model: 'native-tts', input: 'Synthetic audio', format: 'wav', voice: 'native-voice' } },
    UI_MEDIA_STT: { name: 'SpeechToText', args: { model: 'native-asr', audio } },
  };
  return {
    bundleExtra, mediaRecords, toolRequests, materials: { image, video, audio },
    async fetch(input, init) {
      const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
      if (!/^\/t1\/(imagegen|videogen|asr|tts)(\/tasks)?$/.test(url.pathname)) return undefined;
      assert.equal(init?.method, 'POST');
      if (url.pathname.endsWith('/tasks')) return Response.json({ error: { code: 'not_found', message: 'Synthetic platform exercises the original synchronous compatibility route.' } }, { status: 404 });
      const body = JSON.parse(String(init?.body));
      assert.ok(mediaRecords.length < 100, 'bounded synthetic media requests');
      const face = url.pathname.split('/').at(-1);
      if (face === 'imagegen' || face === 'videogen') assert.equal(typeof body.prompt, 'string');
      let audioMetadata;
      if (face === 'asr') {
        assert.ok(typeof body.audio === 'string' && body.audio.startsWith('data:audio/'));
        const bytes = Buffer.from(body.audio.split(',')[1], 'base64');
        try {
          assert.ok(bytes.length > 0 && bytes.length <= 16 * 1024 * 1024, 'bounded original audio input');
          audioMetadata = { audioBytes: bytes.length, audioSha256: createHash('sha256').update(bytes).digest('hex'), wave: inspectWave(bytes) };
        } finally { bytes.fill(0); } // Record only format/length/hash, never captured PCM or base64.
      }
      if (face === 'tts') { assert.equal(typeof body.input, 'string'); assert.ok(body.input.length <= 16); }
      mediaRecords.push({ path: url.pathname, model: body.model, input: body.input, ...audioMetadata,
        prompt: body.prompt, at: new Date().toISOString(), synthetic: true });
      await mkdir(directory, { recursive: true });
      await writeFile(join(directory, 'native-media-records.json'), JSON.stringify(mediaRecords));
      if (body.input === 'LOSS') throw new Error('Synthetic media response lost after acceptance');
      if (body.input === 'CANCEL') await new Promise((resolve, reject) => {
        const signal = init?.signal;
        const cancel = () => { clearTimeout(timer); reject(new DOMException('Synthetic accepted media request cancelled', 'AbortError')); };
        const timer = setTimeout(() => { signal?.removeEventListener('abort', cancel); resolve(); }, 12000);
        if (signal?.aborted) cancel(); else signal?.addEventListener('abort', cancel, { once: true });
      });
      if (face === 'imagegen') return Response.json({ model: 'native-image', images: [{ url: body.prompt === 'Synthetic bad image' ? 'https://denied.example.invalid/no.png' : image }], imageCount: 1 });
      if (face === 'videogen') return Response.json({ model: 'native-video', videos: [{ url: video }], videoCount: 1, billedSeconds: body.duration ?? 2 });
      if (face === 'asr') {
        // The UI driver proves which input produced this payload. This upstream only
        // identifies the current WAV; it does not claim recognition accuracy or tone routing.
        const currentPcm = audioMetadata.audioSha256 !== fixtureWaveSha256 && audioMetadata.wave?.recorderLayout === true;
        return Response.json({ model: 'native-asr', text: currentPcm ? `UI recording draft 中文 ${audioMetadata.audioSha256.slice(0, 12)}` : 'UI transcription draft 中文',
          language: 'zh', durationSec: (audioMetadata.wave?.durationMs ?? 1000) / 1000 });
      }
      return Response.json({ model: 'native-tts', billedChars: body.input.length, audio: { b64: wav.toString('base64'), mime: 'audio/wav', format: 'wav', durationMs: 1000, sampleRate: 16000 } });
    },
  };
}

function inspectWave(bytes) {
  if (bytes.length < 44 || bytes.toString('ascii', 0, 4) !== 'RIFF' || bytes.toString('ascii', 8, 12) !== 'WAVE' || bytes.readUInt32LE(4) + 8 !== bytes.length) return null;
  let format, pcmBytes, dataOffset;
  for (let offset = 12; offset + 8 <= bytes.length;) {
    const name = bytes.toString('ascii', offset, offset + 4), size = bytes.readUInt32LE(offset + 4), start = offset + 8;
    if (start + size > bytes.length) return null;
    if (name === 'fmt ' && size >= 16) format = { formatTag: bytes.readUInt16LE(start), channels: bytes.readUInt16LE(start + 2),
      sampleRate: bytes.readUInt32LE(start + 4), byteRate: bytes.readUInt32LE(start + 8), blockAlign: bytes.readUInt16LE(start + 12), bitsPerSample: bytes.readUInt16LE(start + 14) };
    if (name === 'data') { pcmBytes = size; dataOffset = start; }
    offset = start + size + (size % 2);
  }
  if (!format || pcmBytes === undefined || format.byteRate === 0) return null;
  return { ...format, pcmBytes, dataOffset, durationMs: Math.round(pcmBytes * 1000 / format.byteRate),
    recorderLayout: dataOffset === 44 && format.formatTag === 1 && format.channels === 1 && format.sampleRate === 16000 &&
      format.byteRate === 32000 && format.blockAlign === 2 && format.bitsPerSample === 16 && pcmBytes > 0 && pcmBytes % 2 === 0 };
}
