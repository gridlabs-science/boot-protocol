# boot-protocol
A decentralized protocol for bitcoin hashing nodes to share block rewards and reduce variance

# The Problem
Block construction, and therefore transaction selection are laughably centralized.  The vast majority of new coinbase rewards go to one of only a handful of wallets.  
This happened because centralized, low variance payout structures (FPPS) can outcompete higher variance methods like PPLNS.  It is difficult for any small competing pool to get past the minimum hashrate required to have a manageable variance.  

# Prior Art
The best known example is P2Pool.  This used a secondary blockchain with faster blocktimes to track individual shares in a decentralized manner.  P2Pool failed largely for two reasons.
1. The extra overhead required to run the secondary blockchain was cumbersome
2. The 30-second block times excacerbated the negative effects of block propagation time.  In all blockchains, when a new block is found it takes a brief amount of time before the rest of the network knows about the new block.  This means that nodes which discover the new block first have an advantage of being able to work on building on the new block before the rest of the network finds out.  If the difference between average block times and block propogation speed is great, than this effect is negligible, for example Bitcoin's ~10 minute block times (600 seconds) vs. about 6 seconds for network propogation.  However if block times are very fast (eg. 30 seconds), then physically centralizing hashrate becomes quite advantageous.  Nodes near the network center will earn significantly higher rewards.
The second (upcoming) solution is Braid Pool, an exciting new decentralized pool which solves the second problem (block propogation advantage).  Braid Pool is a much more advanced attempt using Directed Acyclic Graphs (akin to Kaspa) to eliminate the block propegation problem while still having very fast "block" times on the share-chain (about 1 second in theory).  In theory a block witholding attack would reduce miner revenue.  The pool itself could be 51% attacked, so this requires additional complexity to protect against.  
The third (also upcoming) solution is Ocean Pool.  Ocean Pool of course is already operating, and allows hashers the choice of three different templates.  They are working hard on adding the ability for miners to build their own templates, which will solve the problem of centralized block template creation.  Being a centralized entity themselves (albeit with nicely decentralized block template construction), there is the black swan risk that Ocean could get shut down by regulators.  Miners on Ocean also run the risk of reduced revenue from a block witholding attack.  
Boot Protocol is named a protocol, not a pool, because it is attacking the variance "problem" from the other side.  All pools attempt to estimate the actual amount of effort a miner puts in by tracking shares, either from centralized servers (Ocean) or using a side chain (P2Pool and Braid Pool).  Boot Protocol never attempts to track actual work inputs, and never centralizes the block rewards at all.  Conceptually, Boot Protocol is much closer to Solo mining, albiet with up to 16x reduced variance.

# Advantages of Boot Protocol
Compared to solo mining, Boot Protocol should have up to 16x reduced variance.  
Compared to standard pooled mining (eg. Ocean with sovereign block template construction), Boot Protocol should offer reduced bandwidth requirements and much more resiliance to regulatory attack.
Compared to decentralized pooled mining, Boot Protocol should have reduced bandwidth requirements, reduced computational overhead, and a vastly simpler code base.  

# Block Witholding attacks
Boot Protocol should be far more resistant against (and possibly immune to?) block witholding attacks.  If proven, this would be the only known method sharing block rewards in a decentralized permissionless manner that is not vulnerable to block witholding.  

# Boot Protocol ELI5
Key terms:
- Winners List:  A list of 15 addresses that have provided the highest difficulty hashes on the current template.  These (with the miner's own address) are used to create the coinbase transaction for the next round.  This list is finalized once a new real Bitcoin block is found.
- Boot Protocol Message (BPM): This consists of the block header, and just enough information from the Merkle Tree that other nodes can verify the addresses listed in the coinbase transaction
- WL Threshold Difficulty: Defined as 1/2 the difficulty of the lowest difficulty proof from the previous round's Winners List. This threshold could be raised to reduce bandwidth requirements, or lowered if necessary.

1. Create a block template using a local Bitcoin node.  The Coinbase payout should split the block reward evenly between your own address and the 15 addresses in the primary Winners List (see later steps).  If the WL is <15 long, divide the rewards equally between however many addresses are on the list.  Your own address is always spot '0'.
2. Start hashing on this template.  Once the first solution is found that meets the WL Threshold Difficulty, move to step 3:
3. Create a Boot Protocol Message.  Using the found solution (which is just a Bitcoin block with a lower difficulty target), broadcast this message to other nodes.
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

# Plain Language Discussion
Coming soon.
