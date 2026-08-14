import { useParams } from 'wouter'

// Placeholder. The booking page is built here.
export default function Book() {
  const { slug } = useParams()
  return <main>{slug}</main>
}
