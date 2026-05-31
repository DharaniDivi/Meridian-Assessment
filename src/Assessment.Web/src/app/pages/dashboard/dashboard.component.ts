import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';
import {
  AssessmentApiService,
  Layer1Result,
  Layer2Result,
  Layer3Result,
  Layer4Result,
  SubmissionResult
} from '../../services/assessment-api.service';
import { SUBMISSION_TYPES } from '../../models/submission-types';

interface LogEntry {
  time: string;
  level: 'info' | 'error' | 'success';
  message: string;
}

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit, OnDestroy {
  readonly loading = signal(false);
  readonly configStatus = signal<string>('Checking…');
  readonly healthBody = signal<string | null>(null);
  readonly timeRemaining = signal<string>('—');
  readonly layer1 = signal<Layer1Result | null>(null);
  readonly layer2 = signal<Layer2Result | null>(null);
  readonly layer3 = signal<Layer3Result | null>(null);
  readonly layer4 = signal<Layer4Result | null>(null);
  readonly candidates = signal<string[]>([]);
  readonly validSubmissionTypes = signal<string[]>([]);
  readonly logs = signal<LogEntry[]>([]);

  submitType: string = SUBMISSION_TYPES.contentHash;
  submitValue = '';
  submitNotes = '';

  private timerHandle?: ReturnType<typeof setInterval>;

  constructor(private readonly api: AssessmentApiService) {}

  ngOnInit(): void {
    this.loadConfigStatus();
    this.checkHealth();
    this.refreshTime();
    this.timerHandle = setInterval(() => this.refreshTime(), 30_000);
  }

  loadConfigStatus(): void {
    this.api.getConfigStatus().subscribe({
      next: (status) => {
        const parts = [
          status.baseUrlConfigured ? `URL: ${status.baseUrl}` : 'URL: not configured',
          status.apiKeyConfigured ? 'API key: configured' : 'API key: missing'
        ];
        this.configStatus.set(parts.join(' · '));
      },
      error: () => this.configStatus.set('Backend not reachable')
    });
  }

  ngOnDestroy(): void {
    if (this.timerHandle) {
      clearInterval(this.timerHandle);
    }
  }

  checkHealth(): void {
    this.run('Health check', () => this.api.getHealth(), (result) => {
      this.healthBody.set(result.body ?? JSON.stringify(result));
      this.log(result.isHealthy ? 'success' : 'error', `Health: ${result.statusCode}`);
    });
  }

  refreshTime(): void {
    this.api.getTimeRemaining().subscribe({
      next: (result) => {
        if (result.remaining) {
          this.timeRemaining.set(result.remaining);
          return;
        }

        if (result.remainingSeconds != null) {
          const h = Math.floor(result.remainingSeconds / 3600);
          const m = Math.floor((result.remainingSeconds % 3600) / 60);
          const s = result.remainingSeconds % 60;
          this.timeRemaining.set(`${h}h ${m}m ${s}s`);
          return;
        }

        if (!result.success) {
          this.timeRemaining.set(result.message ?? result.hint ?? 'Time unavailable — track manually from first authenticated call');
          return;
        }

        this.timeRemaining.set(result.message ?? 'Unknown format');
      },
      error: () => this.timeRemaining.set('Backend unreachable — track your 3-hour window manually')
    });
  }

  discover(): void {
    this.run('Discover endpoints', () => this.api.discoverEndpoints(), (items) => {
      this.log('info', items.join(' | '));
    });
  }

  runLayer1(): void {
    this.run('Layer 1 fetch+hash', () => this.api.runLayer1(), (result) => {
      this.layer1.set(result);
      if (result.hashHex) {
        this.submitType = SUBMISSION_TYPES.contentHash;
        this.submitValue = result.hashHex;
      }
      if (result.hashes) {
        this.log('info', `Layer 1 hash formats: ${Object.keys(result.hashes).join(', ')}`);
      }
      if (result.message) {
        this.log('info', result.message);
      }
    });
  }

  rehashLayer1(): void {
    this.run('Layer 1 rehash', () => this.api.rehashLayer1(), (result) => {
      this.layer1.set(result);
      if (result.hashHex) {
        this.submitType = SUBMISSION_TYPES.contentHash;
        this.submitValue = result.hashHex;
      }
    });
  }

  runLayer2(): void {
    this.run('Layer 2 decrypt', () => this.api.runLayer2(), (result) => {
      this.layer2.set(result);
      if (result.hashHex) {
        this.submitType = SUBMISSION_TYPES.decryptedHash;
        this.submitValue = result.hashHex;
      }
    });
  }

  runLayer3(): void {
    this.run('Layer 3 search', () => this.api.runLayer3(), (result) => {
      this.layer3.set(result);
      if (result.answer) {
        this.submitType = SUBMISSION_TYPES.algorithmAnswer;
        this.submitValue = result.answer;
      }
    });
  }

  loadCandidates(): void {
    this.run('Layer 3 candidates', () => this.api.getLayer3Candidates(), (items) => this.candidates.set(items));
  }

  runLayer4(): void {
    this.run('Layer 4 analysis', () => this.api.runLayer4(), (result) => {
      this.layer4.set(result);
      if (result.analysis) {
        this.submitType = SUBMISSION_TYPES.analysis;
        this.submitValue = result.analysis;
      }
    });
  }

  discoverSubmissionTypes(): void {
    this.run('Discover submission types', () => this.api.getSubmissionTypes(), (types) => {
      this.validSubmissionTypes.set(types);
      if (types.length) {
        this.submitType = types.find(t => /content.?hash|integrity|hash/i.test(t)) ?? types[0];
      }
      this.log('info', types.length ? `Valid types: ${types.join(', ')}` : 'No types returned');
    });
  }

  submitManual(): void {
    this.run('Submit answer', () => this.api.submit({
      type: this.submitType,
      value: this.submitValue,
      notes: this.submitNotes || undefined
    }), (result: SubmissionResult) => this.logSubmitResult('Submit', this.submitType, result));
  }

  submitLayer1Quick(): void {
    this.run('Submit layer1', () => this.api.submitLayer1(), (result) =>
      this.logSubmitResult('Layer1 submit', SUBMISSION_TYPES.contentHash, result));
  }

  submitLayer2Quick(): void {
    this.run('Submit layer2', () => this.api.submitLayer2(), (result) =>
      this.logSubmitResult('Layer2 submit', SUBMISSION_TYPES.decryptedHash, result));
  }

  submitLayer3Quick(): void {
    this.run('Submit layer3', () => this.api.submitLayer3(), (result) =>
      this.logSubmitResult('Layer3 submit', SUBMISSION_TYPES.algorithmAnswer, result));
  }

  submitLayer4Quick(): void {
    this.run('Submit layer4', () => this.api.submitLayer4(), (result) =>
      this.logSubmitResult('Layer4 submit', SUBMISSION_TYPES.analysis, result));
  }

  useLayer1Hash(name: string, hash: string): void {
    this.submitType = SUBMISSION_TYPES.contentHash;
    this.submitValue = hash;
    this.log('info', `Selected ${name} hash for submit.`);
  }

  layer1HashEntries(hashes?: Record<string, string>): Array<{ name: string; hash: string }> {
    if (!hashes) {
      return [];
    }

    return Object.entries(hashes).map(([name, hash]) => ({ name, hash }));
  }

  private logSubmitResult(label: string, type: string, result: SubmissionResult): void {
    const level = result.success ? 'success' : 'error';
    const detail = this.formatSubmissionMessage(result.message);
    const valid = result.validTypes?.length ? ` Valid types: ${result.validTypes.join(', ')}` : '';
    const alternates = result.alternateHashes?.length
      ? ` Alternates: ${result.alternateHashes.join(', ')}`
      : '';
    const format = result.primaryFormat ? ` format=${result.primaryFormat}` : '';
    this.log(level, `${label} (${type}): ${result.statusCode}${format} ${detail}${valid}${alternates}`);
    if (result.alternateHashes?.length && !result.success) {
      this.log('info', `Try manual submit with an alternate hash from the list above.`);
    }
    if (result.validTypes?.length) {
      this.validSubmissionTypes.set(result.validTypes);
    }
  }

  private formatSubmissionMessage(message?: string): string {
    if (!message) {
      return '';
    }

    try {
      const parsed = JSON.parse(message) as { message?: string; correct?: boolean };
      if (parsed.message) {
        return parsed.correct === false ? `${parsed.message} (incorrect)` : parsed.message;
      }
    } catch {
      // plain text
    }

    return message;
  }

  private formatError(label: string, err: unknown): string {
    const body = (err as { error?: Partial<SubmissionResult> & { error?: string } })?.error;
    const message = body?.message ?? body?.error ?? (err as Error)?.message ?? String(err);
    const validTypes = body?.validTypes;
    const valid = validTypes?.length ? ` Valid types: ${validTypes.join(', ')}` : '';
    if (validTypes?.length) {
      this.validSubmissionTypes.set(validTypes);
      this.submitType = validTypes.find(t => /content.?hash|integrity|hash/i.test(t)) ?? validTypes[0];
    }
    return `${label} failed: ${message}${valid}`;
  }

  private run<T>(label: string, action: () => Observable<T>, onSuccess: (value: T) => void): void {
    this.loading.set(true);
    this.log('info', `Starting: ${label}`);
    action().subscribe({
      next: (value) => {
        onSuccess(value);
        this.log('success', `Done: ${label}`);
        this.loading.set(false);
      },
      error: (err) => {
        this.log('error', this.formatError(label, err));
        this.loading.set(false);
      }
    });
  }

  private log(level: LogEntry['level'], message: string): void {
    const entry: LogEntry = {
      time: new Date().toLocaleTimeString(),
      level,
      message
    };
    this.logs.update(items => [entry, ...items].slice(0, 100));
  }
}
