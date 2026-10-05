package segment

import (
	"context"
	"testing"

	"github.com/klauspost/compress/zstd"
)

func TestEncodeAllKeepsKeyframeSeparateFromDeltas(t *testing.T) {
	jobs := make(chan Job, 1)
	jobs <- Job{ID: 7, FirstTick: 100, LastTick: 199, Keyframe: []byte("state"), Deltas: []byte("updates")}
	close(jobs)

	result := <-EncodeAll(context.Background(), 2, jobs)
	decoder, err := zstd.NewReader(nil)
	if err != nil {
		t.Fatal(err)
	}
	defer decoder.Close()

	keyframe, err := decoder.DecodeAll(result.Keyframe, nil)
	if err != nil || string(keyframe) != "state" {
		t.Fatalf("bad keyframe: %q, %v", keyframe, err)
	}
	deltas, err := decoder.DecodeAll(result.Deltas, nil)
	if err != nil || string(deltas) != "updates" {
		t.Fatalf("bad deltas: %q, %v", deltas, err)
	}
}
