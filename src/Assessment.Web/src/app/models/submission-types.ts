export const SUBMISSION_TYPES = {
  contentHash: 'content_hash',
  decryptedHash: 'decrypted_hash',
  algorithmAnswer: 'algorithm_answer',
  analysis: 'analysis',
  repo: 'repo',
  transcript: 'transcript'
} as const;

export type SubmissionType = (typeof SUBMISSION_TYPES)[keyof typeof SUBMISSION_TYPES];

export const ALL_SUBMISSION_TYPES: string[] = [
  SUBMISSION_TYPES.contentHash,
  SUBMISSION_TYPES.decryptedHash,
  SUBMISSION_TYPES.algorithmAnswer,
  SUBMISSION_TYPES.analysis,
  SUBMISSION_TYPES.repo,
  SUBMISSION_TYPES.transcript
];
