import { useEffect, useState } from 'react'

/** Who runs this instance, as `GET /api/v1/legal` answers it. Each is null when unset. */
export type LegalDetails = {
  operatorName: string | null
  contactEmail: string | null
  jurisdiction: string | null
}

export const NO_DETAILS: LegalDetails = { operatorName: null, contactEmail: null, jurisdiction: null }

/** The first mention of an unnamed operator; later mentions shorten to {@link SHORT_OPERATOR}. */
export const FALLBACK_OPERATOR = 'the operator of this Kairos instance'
export const SHORT_OPERATOR = 'the operator'
export const FALLBACK_JURISDICTION = 'the country where the operator is based'

/** Google requires this sentence verbatim, on the home page or in the privacy policy. */
export const LIMITED_USE =
  "Kairos's use and transfer of information received from Google APIs to any other app will adhere to the Google API Services User Data Policy, including the Limited Use requirements."

/**
 * A failed read is the same as no details. The text is complete either way,
 * so a reviewer on a slow connection never meets a half-written policy.
 */
export function useLegalDetails(): LegalDetails {
  const [details, setDetails] = useState(NO_DETAILS)
  useEffect(() => {
    const controller = new AbortController()
    fetch('/api/v1/legal', { signal: controller.signal })
      .then((response) => (response.ok ? response.json() : NO_DETAILS))
      .then((body: Partial<LegalDetails>) => setDetails({ ...NO_DETAILS, ...body }))
      .catch(() => {})
    return () => controller.abort()
  }, [])
  return details
}

export function capitalised(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1)
}
