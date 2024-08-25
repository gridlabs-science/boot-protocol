# boot-protocol
A decentralized protocol for bitcoin hashing nodes to share block rewards and reduce variance

## The Problem
Block construction, and therefore transaction selection are laughably centralized.  The vast majority of new coinbase rewards go to one of only a handful of wallets.  
This happened because centralized, low variance payout structures (FPPS) can outcompete higher variance methods like PPLNS.  It is difficult for any small competing pool to get past the minimum hashrate required to have a manageable variance.  

## Prior Art
The best known example is P2Pool.  This used a secondary blockchain with faster blocktimes to track individual shares in a decentralized manner.  P2Pool failed largely for two reasons.
1. The extra overhead required to run the secondary blockchain was cumbersome
2. The 30-second block times excacerbated the negative effects of block propagation time.  In all blockchains, when a new block is found it takes a brief amount of time before the rest of the network knows about the new block.  This means that nodes which discover the new block first have an advantage of being able to work on building on the new block before the rest of the network finds out.  If the difference between average block times and block propogation speed is great, than this effect is negligible, for example Bitcoin's ~10 minute block times (600 seconds) vs. about 6 seconds for network propogation.  However if block times are very fast (eg. 30 seconds), then physically centralizing hashrate becomes quite advantageous.  Nodes near the network center will earn significantly higher rewards.

The second (upcoming) solution is Braid Pool, an exciting new decentralized pool which solves the second problem (block propogation advantage).  Braid Pool is a much more advanced attempt using Directed Acyclic Graphs (akin to Kaspa) to eliminate the block propegation problem while still having very fast "block" times on the share-chain (about 1 second in theory).  In theory a block witholding attack would reduce miner revenue.  The pool itself could be 51% attacked, so this requires additional complexity to protect against.  

The third (also upcoming) solution is Ocean Pool.  Ocean Pool of course is already operating, and allows hashers the choice of three different templates.  They are working hard on adding the ability for miners to build their own templates, which will solve the problem of centralized block template creation.  Being a centralized entity themselves (albeit with nicely decentralized block template construction), there is the black swan risk that Ocean could get shut down by regulators.  Miners on Ocean also run the risk of reduced revenue from a block witholding attack.  

Boot Protocol is named a protocol, not a pool, because it is attacking the variance "problem" from the other side.  All pools attempt to estimate the actual amount of effort a miner puts in by tracking shares, either from centralized servers (Ocean) or using a side chain (P2Pool and Braid Pool).  Boot Protocol never attempts to track actual work inputs, and never centralizes the block rewards at all.  Conceptually, Boot Protocol is much closer to Solo mining, albiet with up to 16x reduced variance.  This concept accepts some variance, in exchange for other advantages.

## Advantages of Boot Protocol
Compared to solo mining, Boot Protocol should have up to 16x reduced variance.  
Compared to standard pooled mining (eg. Ocean with sovereign block template construction), Boot Protocol should offer reduced bandwidth requirements and much more resiliance to regulatory attack.
Compared to decentralized pooled mining, Boot Protocol should have reduced bandwidth requirements, reduced computational overhead, and a vastly simpler code base.  

### Block Witholding attacks (killer feature?)
Boot Protocol should be far more resistant against *(and possibly immune to?) block witholding attacks*.  If proven, this would be the only known method of sharing block rewards in a decentralized permissionless manner that is not vulnerable to block witholding.  

---
## Boot Protocol Pseudocode
Key terms:
- Winners List:  A list of 15 addresses that have provided the highest difficulty hashes on the current template.  These (with the miner's own address) are used to create the coinbase transaction for the next round.  This list is finalized once a new real Bitcoin block is found.
- Boot Protocol Message (BPM): This consists of the block header, and just enough information from the Merkle Tree that other nodes can verify the addresses listed in the coinbase transaction
- WL Threshold Difficulty: Defined as 1/2 the difficulty of the lowest difficulty proof from the previous round's Winners List. This threshold could be raised to reduce bandwidth requirements, or lowered if necessary.
- Team: In this context, a team is the loose grouping of miners that are all working on templates built from the same Winners List.  They are sharing their proofs with each other, attempting to get on the next Winners List

1. Create a block template using a local Bitcoin node.  The Coinbase payout should split the block reward evenly between your own address and the 15 addresses in the primary Winners List (see later steps).  If the WL has less than 15 entries (not including your own address in spot 0), divide the rewards equally between however many addresses are on the list.  Your own address is always spot '0'.
2. Start hashing on this template.  Once the first solution is found that meets the WL Threshold Difficulty, move to step 3:
3. Create a Boot Protocol Message.  Using the found solution (which is just a Bitcoin block with a lower difficulty target), broadcast this proof to other nodes.
4. Continue hashing while listening for other BPMs.
   If one is recieved, validate it:
     It must be a valid Bitcoin block, albeit with lower difficulty.
     If spots 1-15 of the coinbase transaction match our own template's, then this proof of work is from our team.
       Check the difficulty against our current Winner's List.
       If it is better than any of those in spots 1-15, then insert it into the list in the appropriate spot and remove the lowest difficulty proof from the WL.  Then re-broadcast it to other nodes.
       Else if it is lower than spot 15, ignore it.  It this happens repeatedly, then this node might be trying a DOS attack, consider disconnecting or blacklisting them.
     If spots 1-15 of the coinbase do NOT match our own template's, then this proof of work is from a different team also using the Boot Protocol.  Create a second (or 3rd, or 4th) Winner's List, and add this proof of work to it, and finally re-broadcast the message.
   If we have created multiple Winner's Lists, periodically check which list has the highest total difficulty.  Sum up the difficulty from each of the 15 proofs on each list.  Select the one with the highest total difficulty as the primary list.  Note:  This might need to be highest average difficulty, or highest median dificulty.  I'm not sure yet, and not good enough at statistics to figure it out.  
5. When a new real Bitcoin block is found, freeze the Winner's List(s).  Only keep the primary list, secondary lists can be deleted.  Go to Step 1.
---
## Plain Language Discussion
Imagine 16 frens who want to get into mining.  They each buy an identical 1TH Bitaxe to start solo hashing.  They don't trust pools, but they'd rather have 16x better odds of getting 1/16 of a block, so they all agree to put each other's addresses in the coinbase split evenly.  Simple.  They have now reduced their variance.  

Next, one of their Bitaxes dies, and gets replaced by a 15TH Future Bit, bringing their team to 30TH.  But since that machine is putting out 50% of the team's hashpower, they all agree that the block reward should be 50% his, and the rest gets split by the 15 Bitaxes.  

After that, a few of the frens start experiencing power issues and aren't on 100% of the time.  Being good Bitcoiners, they decide to verify, not trust.  They agree to send each other their block solution proofs (shares) for each new block they work on.  If someone doesn't submit a share for the current block, then they must have turned off and get cut out of the coinbase split for the next block.  

Over time they notice that if they list out all the shares sorted by difficulty for each block attempt, the Future Bit produces about 8 of the top 16 shares.  They also get 100's more frens that want to join.  Since they don't want to split the coinbase 100's of ways, they decide to run a little competition.  Whoever can produce one of the top 15 shares gets their address in the coinbase of the team's next attempted block.  Those who have bigger machines, or stay on 24/7, have a lot more chances to get high difficulties, and so end up in the coinbase of block attempts more often.  The little Bitaxes can still play, but they have to get pretty lucky.  Not as lucky as pure solo though.  

### What must I do to get paid?
To actually get paid at all, two things must happen.  You have to submit a share to the team which has a top 15 difficulty.  If you control 1/15th of your team's hashrate, then on average you should get into every coinbase they construct.  Sometimes you might win two or three of the top 15 spots, sometimes none.  But on average, you should get one on each round.  Next, someone on your team must actually find a real block of course.  

### Extremes analysis
Sometimes it can help to push an idea to extremes to see how it behaves.  Imagine your team grows in total hashrate to the size of the entire Bitcoin network.  Every single block is won by someone in this team.  Then every coinbase gets split 16 ways.  But now, for a Bitaxe to solo mine a reward, they don't need to hit a block with full network difficulty.  They only need to hit one with 1/15th of that.  If that happens, they will get in the next coinbase template, and thus will get their very lucky reward.  Even in this extreme case, the "centralized" hashrate is no threat to the network because everyone is producing their own templates.  The "team" cannot collude to 51% attack the network, because the only component they are colluding on is the coinbase share.

### Team splits and joins
This algorithm is written to always seek out the most powerful team to join.  That is always in each hasher's best interest because that should give them the lowest variance.  Even if the team's Threshold Difficulty is pretty high, it should be better to have a low chance of getting on a powerful team's coinbase than a high chance of getting on the list with a weak team.  This has the potential to take over the entire network.  

There are edge cases due to network latency.  When a block is found, different miners will see it at different times and thus freeze their current round Winners Lists in different states.  This could happen if a Bitcoin block and a top 15 team share are found at the same time, creating weird race conditions where some nodes include the new share in their Winners List and some do not.  This could split the team in two, effectively creating two separate teams working on different coinbase templates.  They should end up sharing their proofs across the seam.  Nodes that get messages from both teams can track both Winners Lists and then naturally flow over to the stronger team.  

### Layering 
This concept of coinbase splitting can be used as a base layer underneath other standard payout schemes like PPLNS and FPPS.  Any pool or solo miner could use this to "join forces" and decrease their collective variance.  In fact, given the very minimal downsides, they stand to lose out long term against pools and miners that do use this mechanism.  Unfortunately, the only pool that can't benefit from this is Ocean Pool, because they also use the coinbase transaction for payout splitting.  I love Ocean Pool and hope they continue.  I think this protocol and Ocean are serving different needs.  

### Block Witholding thoughts
I suspect this protocol is immune to block witholding attacks.  Given that each miner puts their own address as spot 0 on the Winners List, they have an immediate incentive to go ahead and submit that block to the network if they find a real block and collect their reward.  No value is ever promised, tallied, or accounted long term.  If an adversary consisted of 50% of a team's hashrate, they could expect to claim 50% of the top 15 spots on average and would get 50% of the team's rewards.  However if they chose to block withold, then the team on average would find 50% fewer blocks.  So the attacker would get 1/2 the reward they would have recieved if they'd played honestly.  Lets say though that before an attack a team is winning 1 block per day, and an attacker with an equal amount of power joins and takes 50% of the top 15 spots.  The team's power should have doubled, but because the attacker doesn't submit any blocks, they still win about 1 block per day.  Now unfortunately that 1 block is split almost 50/50 with the attacker.  I say 'almost' because the attacker never gets to be in Spot 0 on the list.  That's for whoever actually finds and submits that real block.  So the attack is costly for everyone, but the honest players should always have a slight advantage in that they always control Spot 0.  This might make detection easier as well.  Someone who consistently gets 7 or 8 top spots but never finds Spot 0 is probably not a fren.
