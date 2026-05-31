import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface HealthResult {
  isHealthy: boolean;
  statusCode: number;
  body?: string;
}

export interface TimeRemainingResult {
  success: boolean;
  statusCode: number;
  remaining?: string;
  remainingSeconds?: number;
  message?: string;
  hint?: string;
}

export interface ConfigStatus {
  baseUrlConfigured: boolean;
  apiKeyConfigured: boolean;
  baseUrl?: string;
}

export interface Layer1Result {
  success: boolean;
  hashHex?: string;
  byteCount: number;
  message?: string;
  hashes?: Record<string, string>;
}

export interface Layer2Result {
  success: boolean;
  recordCount: number;
  hashHex?: string;
  message?: string;
}

export interface Layer3Result {
  success: boolean;
  answer?: string;
  message?: string;
}

export interface Layer4Result {
  success: boolean;
  analysis: string;
  message?: string;
}

export interface SubmissionRequest {
  type: string;
  value: string;
  notes?: string;
}

export interface SubmissionResult {
  success: boolean;
  statusCode: number;
  message?: string;
  validTypes?: string[];
  submittedHash?: string;
  primaryFormat?: string;
  alternateHashes?: string[];
}

@Injectable({ providedIn: 'root' })
export class AssessmentApiService {
  private readonly baseUrl = environment.apiBaseUrl;

  constructor(private readonly http: HttpClient) {}

  getHealth(): Observable<HealthResult> {
    return this.http.get<HealthResult>(`${this.baseUrl}/health`);
  }

  getConfigStatus(): Observable<ConfigStatus> {
    return this.http.get<ConfigStatus>(`${this.baseUrl}/config`);
  }

  getTimeRemaining(): Observable<TimeRemainingResult> {
    return this.http.get<TimeRemainingResult>(`${this.baseUrl}/time`);
  }

  discoverEndpoints(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/discover`);
  }

  getSubmissionTypes(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/submit/types`);
  }

  runLayer1(): Observable<Layer1Result> {
    return this.http.post<Layer1Result>(`${this.baseUrl}/layers/1/run`, {});
  }

  getLayer1Hash(): Observable<{ hash: string }> {
    return this.http.get<{ hash: string }>(`${this.baseUrl}/layers/1/hash`);
  }

  runLayer2(): Observable<Layer2Result> {
    return this.http.post<Layer2Result>(`${this.baseUrl}/layers/2/run`, {});
  }

  runLayer3(): Observable<Layer3Result> {
    return this.http.post<Layer3Result>(`${this.baseUrl}/layers/3/run`, {});
  }

  getLayer3Candidates(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/layers/3/candidates`);
  }

  runLayer4(): Observable<Layer4Result> {
    return this.http.post<Layer4Result>(`${this.baseUrl}/layers/4/run`, {});
  }

  rehashLayer1(): Observable<Layer1Result> {
    return this.http.post<Layer1Result>(`${this.baseUrl}/layers/1/rehash`, {});
  }

  submit(request: SubmissionRequest): Observable<SubmissionResult> {
    return this.http.post<SubmissionResult>(`${this.baseUrl}/submit`, request);
  }

  submitLayer1(): Observable<SubmissionResult> {
    return this.http.post<SubmissionResult>(`${this.baseUrl}/submit/layer1`, {});
  }

  submitLayer2(): Observable<SubmissionResult> {
    return this.http.post<SubmissionResult>(`${this.baseUrl}/submit/layer2`, {});
  }

  submitLayer3(): Observable<SubmissionResult> {
    return this.http.post<SubmissionResult>(`${this.baseUrl}/submit/layer3`, {});
  }

  submitLayer4(): Observable<SubmissionResult> {
    return this.http.post<SubmissionResult>(`${this.baseUrl}/submit/layer4`, {});
  }
}
