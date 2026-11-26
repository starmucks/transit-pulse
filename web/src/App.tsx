import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { LiveMap } from './map/LiveMap'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: 1,
    },
  },
})

/**
 * Stage 3.6 shell: live map only. Stop panel and route page arrive in 3.7;
 * TanStack Query is wired for those REST reads.
 */
export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <LiveMap />
    </QueryClientProvider>
  )
}
